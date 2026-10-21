#include "../src/TwocLoadingVulkan.h"
#include <windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
constexpr size_t Count = 10465;
void Require(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}

struct Handle {
    void* value = nullptr;
    ~Handle() { TwmDestroy(value); }
};

struct TestFile {
    std::filesystem::path path;
    ~TestFile() { std::error_code ignored; std::filesystem::remove(path, ignored); }
};

std::vector<char> ModelBytes(const std::vector<float>& weights) {
    std::vector<char> bytes(32 + Count * sizeof(float));
    const char magic[8] = { 'T', 'W', 'O', 'C', 'M', 'L', '1', 0 };
    const uint32_t header[6] = { 1, 96, 96, 8, 16, static_cast<uint32_t>(Count) };
    std::memcpy(bytes.data(), magic, sizeof(magic));
    std::memcpy(bytes.data() + 8, header, sizeof(header));
    std::memcpy(bytes.data() + 32, weights.data(), Count * sizeof(float));
    return bytes;
}

void Write(const std::filesystem::path& path, const std::vector<char>& bytes) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file.write(bytes.data(), static_cast<std::streamsize>(bytes.size()));
    Require(!!file, "Cannot write test model.");
}

std::vector<float> ReferenceLayer(const std::vector<float>& input, const std::vector<float>& weights,
    int width, int inputChannels, int outputChannels, int weightOffset, int biasOffset) {
    // Materialize a full convolution before pooling, independently of shader fusion.
    std::vector<float> convolution(static_cast<size_t>(outputChannels) * width * width);
    for (int oc = 0; oc < outputChannels; ++oc)
        for (int y = 0; y < width; ++y)
            for (int x = 0; x < width; ++x) {
                float value = weights[biasOffset + oc];
                for (int ic = 0; ic < inputChannels; ++ic)
                    for (int ky = 0; ky < 3; ++ky)
                        for (int kx = 0; kx < 3; ++kx) {
                            const int iy = y + ky - 1, ix = x + kx - 1;
                            if (iy >= 0 && iy < width && ix >= 0 && ix < width)
                                value += input[ic * width * width + iy * width + ix]
                                    * weights[weightOffset + (oc * inputChannels + ic) * 9 + ky * 3 + kx];
                        }
                convolution[oc * width * width + y * width + x] = (std::max)(0.0f, value);
            }
    const int pooledWidth = width / 2;
    std::vector<float> pooled(static_cast<size_t>(outputChannels) * pooledWidth * pooledWidth);
    for (int oc = 0; oc < outputChannels; ++oc)
        for (int y = 0; y < pooledWidth; ++y)
            for (int x = 0; x < pooledWidth; ++x) {
                float value = 0;
                for (int dy = 0; dy < 2; ++dy)
                    for (int dx = 0; dx < 2; ++dx)
                        value = (std::max)(value, convolution[oc * width * width + (y * 2 + dy) * width + x * 2 + dx]);
                pooled[oc * pooledWidth * pooledWidth + y * pooledWidth + x] = value;
            }
    return pooled;
}

float Reference(const std::vector<float>& pixels, const std::vector<float>& weights) {
    const auto first = ReferenceLayer(pixels, weights, 96, 1, 8, 0, 72);
    const auto second = ReferenceLayer(first, weights, 48, 8, 16, 80, 1232);
    float logit = weights[10464];
    for (int i = 0; i < 9216; ++i) logit += second[i] * weights[1248 + i];
    const float e = std::exp(-std::abs(logit));
    return logit >= 0 ? 1.0f / (1.0f + e) : e / (1.0f + e);
}

float Predict(void* handle, const std::vector<float>& pixels) {
    char error[1024]{};
    float result = -1;
    if (TwmPredict(handle, pixels.data(), static_cast<unsigned>(pixels.size()), &result, error, sizeof(error)))
        throw std::runtime_error(error);
    Require(std::isfinite(result) && result >= 0 && result <= 1, "Invalid GPU confidence.");
    return result;
}

void Compare(void* handle, const std::vector<float>& weights) {
    std::vector<float> pixels(96 * 96);
    for (unsigned pattern = 0; pattern < 4; ++pattern) {
        for (size_t i = 0; i < pixels.size(); ++i)
            pixels[i] = pattern == 0 ? 0 : pattern == 1 ? 1
                : pattern == 2 ? static_cast<float>((i * 37 + 11) % 997) / 996.0f
                : ((i / 96 + i % 96) % 2 ? 1.0f : 0.0f);
        const float expected = Reference(pixels, weights);
        const float actual = Predict(handle, pixels);
        std::cout << "CPU/GPU pattern " << pattern << ": " << expected << " / " << actual
            << ", absolute error " << std::abs(actual - expected) << '\n';
        Require(std::abs(actual - expected) < 0.00002f, "GPU differs from CPU reference.");
    }
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    try {
        Require(argc >= 2, "Usage: NativeTests.exe <shader-directory> [--validation] [--model <file>]");
        bool validation = false;
        std::filesystem::path externalModel;
        for (int i = 2; i < argc; ++i) {
            if (std::wstring(argv[i]) == L"--validation") validation = true;
            else if (std::wstring(argv[i]) == L"--model" && i + 1 < argc) externalModel = argv[++i];
            else throw std::runtime_error("Unknown test argument.");
        }
        const auto module = GetModuleHandleW(L"TwocLoadingVulkan.dll");
        for (const char* name : { "TwmCreate", "TwmPredict", "TwmGetDeviceName", "TwmDestroy" })
            Require(GetProcAddress(module, name) != nullptr, "Undecorated C ABI export is missing.");
        wchar_t executable[32768];
        const auto pathLength = GetModuleFileNameW(nullptr, executable, 32768);
        Require(pathLength != 0 && pathLength < 32768, "Cannot locate native test executable.");
        TestFile model{ std::filesystem::path(executable).parent_path() / L"native-test-\u00fc.twml" };
        std::vector<float> weights(Count);
        for (size_t i = 0; i < weights.size(); ++i) {
            const float scale = i < 80 ? 0.15f : i < 1248 ? 0.06f : 0.03f;
            weights[i] = std::sin(static_cast<float>(i) * 0.317f) * scale;
        }
        weights[10464] = 0.2f;
        const auto valid = ModelBytes(weights);
        char error[1024]{};
        auto reject = [&](const std::vector<char>& bad) {
            Write(model.path, bad);
            Handle handle;
            Require(TwmCreate(model.path.c_str(), argv[1], validation, &handle.value, error, sizeof(error)) != 0,
                "Invalid model was accepted.");
            Require(!handle.value && error[0], "Model rejection did not clear handle and explain failure.");
        };
        for (size_t offset : { 0u, 8u, 12u, 16u, 20u, 24u, 28u }) {
            auto bad = valid; bad[offset] ^= 0x40; reject(bad);
        }
        auto truncated = valid; truncated.pop_back(); reject(truncated);
        auto extra = valid; extra.push_back(0); reject(extra);
        for (float invalid : { (std::numeric_limits<float>::infinity)(), (std::numeric_limits<float>::quiet_NaN)() }) {
            auto bad = valid; std::memcpy(bad.data() + 32, &invalid, sizeof(invalid)); reject(bad);
        }
        Handle nullHandle;
        Require(TwmCreate(nullptr, argv[1], 0, &nullHandle.value, error, sizeof(error)) != 0, "Null path accepted.");
        Require(TwmCreate(model.path.c_str(), argv[1], 0, nullptr, error, sizeof(error)) != 0, "Null output handle accepted.");
        Write(model.path, valid);
        {
            Handle missingShaders;
            const auto missing = model.path.parent_path() / L"missing-shaders";
            Require(TwmCreate(model.path.c_str(), missing.c_str(), validation, &missingShaders.value, error, sizeof(error)) != 0,
                "Missing shaders were accepted.");
        }
        Handle handle;
        if (TwmCreate(model.path.c_str(), argv[1], validation, &handle.value, error, sizeof(error)))
            throw std::runtime_error(error);
        char name[512]{};
        Require(TwmGetDeviceName(handle.value, name, sizeof(name)) == 0, "Device name unavailable.");
        std::cout << "Vulkan device: " << name << "; validation: " << (validation ? "enabled" : "disabled") << '\n';
        Require(TwmGetDeviceName(nullptr, name, sizeof(name)) != 0, "Null device-name handle accepted.");
        char shortName[2] = { 'x', 'x' };
        Require(TwmGetDeviceName(handle.value, shortName, sizeof(shortName)) != 0 && shortName[1] == 0,
            "Device-name truncation was not bounded.");
        Compare(handle.value, weights);
        std::vector<float> pixels(96 * 96, 0.5f);
        float confidence = -1;
        Require(TwmPredict(handle.value, pixels.data(), 1, &confidence, error, sizeof(error)) != 0, "Bad pixel count accepted.");
        Require(TwmPredict(nullptr, pixels.data(), 9216, &confidence, error, sizeof(error)) != 0, "Null handle accepted.");
        Require(TwmPredict(handle.value, nullptr, 9216, &confidence, error, sizeof(error)) != 0, "Null input accepted.");
        Require(TwmPredict(handle.value, pixels.data(), 9216, nullptr, error, sizeof(error)) != 0, "Null confidence accepted.");
        for (float invalid : { -0.01f, 1.01f, (std::numeric_limits<float>::quiet_NaN)(), (std::numeric_limits<float>::infinity)() }) {
            pixels[0] = invalid;
            char bounded[3] = { 'x', 'x', 'Z' };
            Require(TwmPredict(handle.value, pixels.data(), 9216, &confidence, bounded, 2) != 0,
                "Invalid input pixel accepted.");
            Require(bounded[1] == 0 && bounded[2] == 'Z', "Error buffer bounds violated.");
        }
        pixels[0] = 0.5f;
        const float expected = Reference(pixels, weights);
        std::atomic<bool> threadsPassed{ true };
        std::vector<std::thread> threads;
        for (int i = 0; i < 4; ++i)
            threads.emplace_back([&]() {
                try {
                    for (int repeat = 0; repeat < 8; ++repeat)
                        if (std::abs(Predict(handle.value, pixels) - expected) >= 0.00002f) threadsPassed = false;
                } catch (...) { threadsPassed = false; }
            });
        for (auto& thread : threads) thread.join();
        Require(threadsPassed, "Concurrent prediction failed.");
        const auto start = std::chrono::steady_clock::now();
        for (int i = 0; i < 64; ++i) Predict(handle.value, pixels);
        const auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        std::cout << "64 GPU predictions: " << elapsed << " ms (" << elapsed / 64 << " ms/prediction)\n";
        if (!externalModel.empty()) {
            std::ifstream file(externalModel, std::ios::binary);
            std::vector<char> bytes(32 + Count * sizeof(float));
            file.read(bytes.data(), static_cast<std::streamsize>(bytes.size()));
            Require(!!file, "Cannot read external test model.");
            std::memcpy(weights.data(), bytes.data() + 32, weights.size() * sizeof(float));
            Handle external;
            if (TwmCreate(externalModel.c_str(), argv[1], validation, &external.value, error, sizeof(error)))
                throw std::runtime_error(error);
            std::cout << "External model parity:\n";
            Compare(external.value, weights);
        }
        TwmDestroy(nullptr);
        std::cout << "PASS: parser, C ABI, CPU/GPU parity, repeated dispatch and concurrency.\n";
        return 0;
    } catch (const std::exception& exception) {
        std::cerr << "FAIL: " << exception.what() << '\n';
        return 1;
    }
}
