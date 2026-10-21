#include "TwocLoadingVulkan.h"
#include <vulkan/vulkan.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
constexpr unsigned InputCount = 96 * 96;
constexpr unsigned WeightCount = 10465;
constexpr size_t ModelBytes = 32 + WeightCount * sizeof(float);
// Normal inference is approximately 0.5 ms; allow ample GPU scheduling margin
// while returning control promptly enough to release Game Time on a stalled GPU.
constexpr uint64_t PredictionFenceTimeoutNanoseconds = 1'000'000'000ULL;

bool CopyText(char* destination, unsigned capacity, const char* source) noexcept {
    if (!destination || !capacity) return false;
    const size_t length = std::strlen(source);
    size_t count = (std::min)(length, static_cast<size_t>(capacity - 1));
    // Vulkan device names are UTF-8; never truncate inside a multibyte sequence.
    while (count && (static_cast<unsigned char>(source[count]) & 0xc0) == 0x80) --count;
    std::memcpy(destination, source, count);
    destination[count] = '\0';
    return count == length;
}

void Check(VkResult result, const char* operation) {
    if (result != VK_SUCCESS)
        throw std::runtime_error(std::string(operation) + " failed (Vulkan result "
            + std::to_string(result) + ").");
}

std::vector<char> ReadFile(const std::filesystem::path& path, size_t maximum) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file) throw std::runtime_error("Cannot open model or shader file.");
    const auto length = file.tellg();
    if (length < 0 || static_cast<uint64_t>(length) > maximum)
        throw std::runtime_error("Model or shader file has an invalid size.");
    std::vector<char> bytes(static_cast<size_t>(length));
    file.seekg(0);
    if (!bytes.empty() && !file.read(bytes.data(), static_cast<std::streamsize>(bytes.size())))
        throw std::runtime_error("Cannot read model or shader file.");
    return bytes;
}

std::vector<float> ReadModel(const wchar_t* path) {
    auto bytes = ReadFile(path, ModelBytes + 1);
    if (bytes.size() != ModelBytes)
        throw std::runtime_error("Model must contain exactly 41892 bytes.");
    constexpr char magic[8] = { 'T', 'W', 'O', 'C', 'M', 'L', '1', 0 };
    const uint32_t expected[6] = { 1, 96, 96, 8, 16, WeightCount };
    uint32_t header[6];
    std::memcpy(header, bytes.data() + 8, sizeof(header));
    if (std::memcmp(bytes.data(), magic, sizeof(magic)) != 0
        || std::memcmp(header, expected, sizeof(expected)) != 0)
        throw std::runtime_error("Unsupported model magic, version, dimensions or weight count.");
    std::vector<float> weights(WeightCount);
    std::memcpy(weights.data(), bytes.data() + 32, weights.size() * sizeof(float));
    for (float weight : weights)
        if (!std::isfinite(weight)) throw std::runtime_error("Model weights must be finite.");
    return weights;
}

struct Buffer {
    VkBuffer buffer = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    void* mapped = nullptr;
    VkDeviceSize size = 0;
};

class Runtime {
public:
    std::mutex mutex;
    VkInstance instance = VK_NULL_HANDLE;
    VkPhysicalDevice physicalDevice = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    VkQueue queue = VK_NULL_HANDLE;
    uint32_t queueFamily = 0;
    std::string deviceName;
    VkPhysicalDeviceMemoryProperties memoryProperties{};
    std::array<Buffer, 5> buffers{}; // Input, all weights, pool1, pool2, confidence.
    VkDescriptorSetLayout descriptorLayout = VK_NULL_HANDLE;
    VkPipelineLayout pipelineLayout = VK_NULL_HANDLE;
    VkDescriptorPool descriptorPool = VK_NULL_HANDLE;
    std::array<VkDescriptorSet, 3> descriptorSets{};
    std::array<VkPipeline, 3> pipelines{};
    VkCommandPool commandPool = VK_NULL_HANDLE;
    VkCommandBuffer commands = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    bool failed = false;

    ~Runtime() noexcept {
        if (device) {
            vkDeviceWaitIdle(device);
            if (fence) vkDestroyFence(device, fence, nullptr);
            if (commandPool) vkDestroyCommandPool(device, commandPool, nullptr);
            if (descriptorPool) vkDestroyDescriptorPool(device, descriptorPool, nullptr);
            for (auto pipeline : pipelines)
                if (pipeline) vkDestroyPipeline(device, pipeline, nullptr);
            if (pipelineLayout) vkDestroyPipelineLayout(device, pipelineLayout, nullptr);
            if (descriptorLayout) vkDestroyDescriptorSetLayout(device, descriptorLayout, nullptr);
            for (auto& buffer : buffers) {
                if (buffer.mapped) vkUnmapMemory(device, buffer.memory);
                if (buffer.buffer) vkDestroyBuffer(device, buffer.buffer, nullptr);
                if (buffer.memory) vkFreeMemory(device, buffer.memory, nullptr);
            }
            vkDestroyDevice(device, nullptr);
        }
        if (instance) vkDestroyInstance(instance, nullptr);
    }

    void Initialize(const std::vector<float>& weights, const wchar_t* shaders, bool validation) {
        CreateInstance(validation);
        SelectDevice();
        VkDeviceQueueCreateInfo queueInfo{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
        const float priority = 1.0f;
        queueInfo.queueFamilyIndex = queueFamily;
        queueInfo.queueCount = 1;
        queueInfo.pQueuePriorities = &priority;
        VkDeviceCreateInfo deviceInfo{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
        deviceInfo.queueCreateInfoCount = 1;
        deviceInfo.pQueueCreateInfos = &queueInfo;
        Check(vkCreateDevice(physicalDevice, &deviceInfo, nullptr, &device), "vkCreateDevice");
        vkGetDeviceQueue(device, queueFamily, 0, &queue);
        vkGetPhysicalDeviceMemoryProperties(physicalDevice, &memoryProperties);
        const VkDeviceSize sizes[] = { InputCount * sizeof(float), WeightCount * sizeof(float),
            8 * 48 * 48 * sizeof(float), 16 * 24 * 24 * sizeof(float), sizeof(float) };
        for (size_t i = 0; i < buffers.size(); ++i) CreateBuffer(buffers[i], sizes[i]);
        std::memcpy(buffers[1].mapped, weights.data(), weights.size() * sizeof(float));
        CreateDescriptors();
        const wchar_t* names[] = { L"conv1.spv", L"conv2.spv", L"classifier.spv" };
        for (size_t i = 0; i < pipelines.size(); ++i)
            CreatePipeline(pipelines[i], std::filesystem::path(shaders) / names[i]);
        RecordCommands();
        VkFenceCreateInfo fenceInfo{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        Check(vkCreateFence(device, &fenceInfo, nullptr, &fence), "vkCreateFence");
    }

    float Predict(const float* pixels) {
        if (failed) throw std::runtime_error("The Vulkan runtime is unavailable after a previous GPU failure.");
        std::memcpy(buffers[0].mapped, pixels, InputCount * sizeof(float));
        // Coherent mapped uploads are made visible by queue submission. The recorded
        // host-write barrier also documents their consumption by the first shader.
        VkSubmitInfo submit{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
        submit.commandBufferCount = 1;
        submit.pCommandBuffers = &commands;
        const VkResult submitted = vkQueueSubmit(queue, 1, &submit, fence);
        if (submitted != VK_SUCCESS) {
            failed = true;
            Check(submitted, "vkQueueSubmit");
        }
        // A finite timeout lets the caller release Game Time on a stalled backend.
        // Keep this handle poisoned after any failure: commands may still be running.
        const VkResult completed = vkWaitForFences(device, 1, &fence, VK_TRUE,
            PredictionFenceTimeoutNanoseconds);
        if (completed != VK_SUCCESS) {
            failed = true;
            Check(completed, "vkWaitForFences");
        }
        const VkResult reset = vkResetFences(device, 1, &fence);
        if (reset != VK_SUCCESS) {
            failed = true;
            Check(reset, "vkResetFences");
        }
        float confidence;
        std::memcpy(&confidence, buffers[4].mapped, sizeof(confidence));
        if (!std::isfinite(confidence) || confidence < 0 || confidence > 1)
            throw std::runtime_error("Vulkan inference produced an invalid confidence.");
        return confidence;
    }

private:
    void CreateInstance(bool validation) {
        const char* layer = "VK_LAYER_KHRONOS_validation";
        if (validation) {
            uint32_t count = 0;
            Check(vkEnumerateInstanceLayerProperties(&count, nullptr), "vkEnumerateInstanceLayerProperties");
            std::vector<VkLayerProperties> layers(count);
            Check(vkEnumerateInstanceLayerProperties(&count, layers.data()), "vkEnumerateInstanceLayerProperties");
            if (std::none_of(layers.begin(), layers.end(), [layer](const VkLayerProperties& candidate) {
                    return std::strcmp(candidate.layerName, layer) == 0;
                })) throw std::runtime_error("Requested Vulkan validation layer is not installed.");
        }
        VkApplicationInfo application{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
        application.pApplicationName = "Crash TWoC loading-text inference";
        application.applicationVersion = 1;
        application.apiVersion = VK_API_VERSION_1_0;
        VkInstanceCreateInfo info{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
        info.pApplicationInfo = &application;
        info.enabledLayerCount = validation ? 1u : 0u;
        info.ppEnabledLayerNames = validation ? &layer : nullptr;
        Check(vkCreateInstance(&info, nullptr, &instance), "vkCreateInstance");
    }

    void SelectDevice() {
        uint32_t count = 0;
        Check(vkEnumeratePhysicalDevices(instance, &count, nullptr), "vkEnumeratePhysicalDevices");
        if (!count) throw std::runtime_error("No Vulkan graphics device is available.");
        std::vector<VkPhysicalDevice> devices(count);
        Check(vkEnumeratePhysicalDevices(instance, &count, devices.data()), "vkEnumeratePhysicalDevices");
        int bestScore = -1;
        for (auto candidate : devices) {
            VkPhysicalDeviceProperties properties;
            vkGetPhysicalDeviceProperties(candidate, &properties);
            if (properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU) continue;
            const auto& limits = properties.limits;
            if (limits.maxPerStageDescriptorStorageBuffers < 3 || limits.maxDescriptorSetStorageBuffers < 3
                || limits.maxStorageBufferRange < 8 * 48 * 48 * sizeof(float)
                || limits.maxComputeWorkGroupInvocations < 64 || limits.maxComputeWorkGroupSize[0] < 64
                || limits.maxComputeWorkGroupCount[0] < 288) continue;
            VkPhysicalDeviceMemoryProperties memory;
            vkGetPhysicalDeviceMemoryProperties(candidate, &memory);
            bool coherent = false;
            for (uint32_t i = 0; i < memory.memoryTypeCount; ++i) {
                const auto flags = memory.memoryTypes[i].propertyFlags;
                if ((flags & (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT))
                        == (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)
                    && !(flags & (VK_MEMORY_PROPERTY_DEVICE_COHERENT_BIT_AMD | VK_MEMORY_PROPERTY_DEVICE_UNCACHED_BIT_AMD)))
                    coherent = true;
            }
            if (!coherent) continue;
            uint32_t familyCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, nullptr);
            std::vector<VkQueueFamilyProperties> families(familyCount);
            vkGetPhysicalDeviceQueueFamilyProperties(candidate, &familyCount, families.data());
            for (uint32_t i = 0; i < familyCount; ++i) {
                if (!families[i].queueCount || !(families[i].queueFlags & VK_QUEUE_COMPUTE_BIT)) continue;
                int score = properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU ? 100
                    : properties.deviceType == VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU ? 50 : 0;
                if (!(families[i].queueFlags & VK_QUEUE_GRAPHICS_BIT)) ++score;
                if (score > bestScore) {
                    bestScore = score;
                    physicalDevice = candidate;
                    queueFamily = i;
                    deviceName = properties.deviceName;
                }
            }
        }
        if (!physicalDevice)
            throw std::runtime_error("No hardware Vulkan compute device supports the required buffers and workgroups.");
    }

    void CreateBuffer(Buffer& buffer, VkDeviceSize size) {
        buffer.size = size;
        VkBufferCreateInfo info{ VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        info.size = size;
        info.usage = VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;
        info.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
        Check(vkCreateBuffer(device, &info, nullptr, &buffer.buffer), "vkCreateBuffer");
        VkMemoryRequirements requirements;
        vkGetBufferMemoryRequirements(device, buffer.buffer, &requirements);
        uint32_t selected = UINT32_MAX;
        int bestScore = -1;
        for (uint32_t i = 0; i < memoryProperties.memoryTypeCount; ++i) {
            const auto flags = memoryProperties.memoryTypes[i].propertyFlags;
            if (!(requirements.memoryTypeBits & (1u << i))
                || (flags & (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT))
                    != (VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)
                || (flags & (VK_MEMORY_PROPERTY_DEVICE_COHERENT_BIT_AMD | VK_MEMORY_PROPERTY_DEVICE_UNCACHED_BIT_AMD))) continue;
            const int score = ((flags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) ? 2 : 0)
                + ((flags & VK_MEMORY_PROPERTY_HOST_CACHED_BIT) ? 1 : 0);
            if (score > bestScore) { selected = i; bestScore = score; }
        }
        if (selected == UINT32_MAX)
            throw std::runtime_error("The Vulkan buffer has no compatible host-visible coherent memory type.");
        VkMemoryAllocateInfo allocation{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        allocation.allocationSize = requirements.size;
        allocation.memoryTypeIndex = selected;
        Check(vkAllocateMemory(device, &allocation, nullptr, &buffer.memory), "vkAllocateMemory");
        Check(vkBindBufferMemory(device, buffer.buffer, buffer.memory, 0), "vkBindBufferMemory");
        Check(vkMapMemory(device, buffer.memory, 0, size, 0, &buffer.mapped), "vkMapMemory");
    }

    void CreateDescriptors() {
        std::array<VkDescriptorSetLayoutBinding, 3> bindings{};
        for (uint32_t i = 0; i < bindings.size(); ++i) {
            bindings[i].binding = i;
            bindings[i].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
            bindings[i].descriptorCount = 1;
            bindings[i].stageFlags = VK_SHADER_STAGE_COMPUTE_BIT;
        }
        VkDescriptorSetLayoutCreateInfo layout{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
        layout.bindingCount = static_cast<uint32_t>(bindings.size());
        layout.pBindings = bindings.data();
        Check(vkCreateDescriptorSetLayout(device, &layout, nullptr, &descriptorLayout), "vkCreateDescriptorSetLayout");
        VkPipelineLayoutCreateInfo pipelineInfo{ VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
        pipelineInfo.setLayoutCount = 1;
        pipelineInfo.pSetLayouts = &descriptorLayout;
        Check(vkCreatePipelineLayout(device, &pipelineInfo, nullptr, &pipelineLayout), "vkCreatePipelineLayout");
        VkDescriptorPoolSize poolSize{ VK_DESCRIPTOR_TYPE_STORAGE_BUFFER, 9 };
        VkDescriptorPoolCreateInfo pool{ VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
        pool.maxSets = 3;
        pool.poolSizeCount = 1;
        pool.pPoolSizes = &poolSize;
        Check(vkCreateDescriptorPool(device, &pool, nullptr, &descriptorPool), "vkCreateDescriptorPool");
        const VkDescriptorSetLayout layouts[3] = { descriptorLayout, descriptorLayout, descriptorLayout };
        VkDescriptorSetAllocateInfo allocation{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
        allocation.descriptorPool = descriptorPool;
        allocation.descriptorSetCount = 3;
        allocation.pSetLayouts = layouts;
        Check(vkAllocateDescriptorSets(device, &allocation, descriptorSets.data()), "vkAllocateDescriptorSets");
        const unsigned indices[3][3] = { { 0, 1, 2 }, { 2, 1, 3 }, { 3, 1, 4 } };
        for (uint32_t stage = 0; stage < 3; ++stage) {
            std::array<VkDescriptorBufferInfo, 3> bufferInfo{};
            std::array<VkWriteDescriptorSet, 3> writes{};
            for (uint32_t binding = 0; binding < 3; ++binding) {
                const auto& buffer = buffers[indices[stage][binding]];
                bufferInfo[binding] = { buffer.buffer, 0, buffer.size };
                writes[binding].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
                writes[binding].dstSet = descriptorSets[stage];
                writes[binding].dstBinding = binding;
                writes[binding].descriptorCount = 1;
                writes[binding].descriptorType = VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;
                writes[binding].pBufferInfo = &bufferInfo[binding];
            }
            vkUpdateDescriptorSets(device, 3, writes.data(), 0, nullptr);
        }
    }

    void CreatePipeline(VkPipeline& pipeline, const std::filesystem::path& path) {
        const auto bytes = ReadFile(path, 16 * 1024 * 1024);
        if (bytes.size() < 20 || bytes.size() % sizeof(uint32_t))
            throw std::runtime_error("Shader is not a complete SPIR-V module.");
        std::vector<uint32_t> words(bytes.size() / sizeof(uint32_t));
        std::memcpy(words.data(), bytes.data(), bytes.size());
        if (words[0] != 0x07230203)
            throw std::runtime_error("Shader has an invalid SPIR-V magic.");
        VkShaderModuleCreateInfo moduleInfo{ VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
        moduleInfo.codeSize = bytes.size();
        moduleInfo.pCode = words.data();
        VkShaderModule module = VK_NULL_HANDLE;
        Check(vkCreateShaderModule(device, &moduleInfo, nullptr, &module), "vkCreateShaderModule");
        VkComputePipelineCreateInfo info{ VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO };
        info.stage.sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        info.stage.stage = VK_SHADER_STAGE_COMPUTE_BIT;
        info.stage.module = module;
        info.stage.pName = "main";
        info.layout = pipelineLayout;
        const VkResult result = vkCreateComputePipelines(device, VK_NULL_HANDLE, 1, &info, nullptr, &pipeline);
        vkDestroyShaderModule(device, module, nullptr);
        Check(result, "vkCreateComputePipelines");
    }

    void Barrier(const Buffer& buffer, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess,
        VkPipelineStageFlags sourceStage, VkPipelineStageFlags destinationStage) {
        VkBufferMemoryBarrier barrier{ VK_STRUCTURE_TYPE_BUFFER_MEMORY_BARRIER };
        barrier.srcAccessMask = sourceAccess;
        barrier.dstAccessMask = destinationAccess;
        barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barrier.buffer = buffer.buffer;
        barrier.offset = 0;
        barrier.size = buffer.size;
        vkCmdPipelineBarrier(commands, sourceStage, destinationStage, 0, 0, nullptr, 1, &barrier, 0, nullptr);
    }

    void RecordCommands() {
        VkCommandPoolCreateInfo pool{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
        pool.queueFamilyIndex = queueFamily;
        Check(vkCreateCommandPool(device, &pool, nullptr, &commandPool), "vkCreateCommandPool");
        VkCommandBufferAllocateInfo allocation{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
        allocation.commandPool = commandPool;
        allocation.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        allocation.commandBufferCount = 1;
        Check(vkAllocateCommandBuffers(device, &allocation, &commands), "vkAllocateCommandBuffers");
        VkCommandBufferBeginInfo begin{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        Check(vkBeginCommandBuffer(commands, &begin), "vkBeginCommandBuffer");
        for (unsigned i : { 0u, 1u })
            Barrier(buffers[i], VK_ACCESS_HOST_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT,
                VK_PIPELINE_STAGE_HOST_BIT, VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT);
        const uint32_t groups[3] = { 288, 144, 1 };
        for (uint32_t stage = 0; stage < 3; ++stage) {
            vkCmdBindPipeline(commands, VK_PIPELINE_BIND_POINT_COMPUTE, pipelines[stage]);
            vkCmdBindDescriptorSets(commands, VK_PIPELINE_BIND_POINT_COMPUTE, pipelineLayout,
                0, 1, &descriptorSets[stage], 0, nullptr);
            vkCmdDispatch(commands, groups[stage], 1, 1);
            Barrier(buffers[stage + 2], VK_ACCESS_SHADER_WRITE_BIT,
                stage < 2 ? VK_ACCESS_SHADER_READ_BIT : VK_ACCESS_HOST_READ_BIT,
                VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT,
                stage < 2 ? VK_PIPELINE_STAGE_COMPUTE_SHADER_BIT : VK_PIPELINE_STAGE_HOST_BIT);
        }
        Check(vkEndCommandBuffer(commands), "vkEndCommandBuffer");
    }
};
} // namespace

int __cdecl TwmCreate(const wchar_t* model_path, const wchar_t* shader_directory,
    int enable_validation, void** handle, char* error, unsigned capacity) {
    CopyText(error, capacity, "");
    if (handle) *handle = nullptr;
    try {
        if (!handle || !model_path || !*model_path || !shader_directory || !*shader_directory)
            throw std::runtime_error("A model path, shader directory and output handle are required.");
        const auto weights = ReadModel(model_path);
        auto runtime = std::make_unique<Runtime>();
        runtime->Initialize(weights, shader_directory, enable_validation != 0);
        *handle = runtime.release();
        return 0;
    } catch (const std::exception& exception) {
        CopyText(error, capacity, exception.what());
    } catch (...) {
        CopyText(error, capacity, "Unexpected native Vulkan initialization failure.");
    }
    return 1;
}

int __cdecl TwmPredict(void* handle, const float* pixels, unsigned pixel_count,
    float* confidence, char* error, unsigned capacity) {
    CopyText(error, capacity, "");
    if (confidence) *confidence = 0;
    try {
        if (!handle || !pixels || !confidence || pixel_count != InputCount)
            throw std::runtime_error("Prediction requires a handle, 9216 pixels and an output confidence.");
        auto& runtime = *static_cast<Runtime*>(handle);
        std::lock_guard<std::mutex> lock(runtime.mutex);
        for (unsigned i = 0; i < pixel_count; ++i)
            if (!std::isfinite(pixels[i]) || pixels[i] < 0 || pixels[i] > 1)
                throw std::runtime_error("Input pixels must be finite and in [0, 1].");
        *confidence = runtime.Predict(pixels);
        return 0;
    } catch (const std::exception& exception) {
        CopyText(error, capacity, exception.what());
    } catch (...) {
        CopyText(error, capacity, "Unexpected native Vulkan prediction failure.");
    }
    return 1;
}

int __cdecl TwmGetDeviceName(void* handle, char* name, unsigned capacity) {
    CopyText(name, capacity, "");
    try {
        if (!handle || !name || !capacity) return 1;
        auto& runtime = *static_cast<Runtime*>(handle);
        std::lock_guard<std::mutex> lock(runtime.mutex);
        return CopyText(name, capacity, runtime.deviceName.c_str()) ? 0 : 1;
    } catch (...) { return 1; }
}

void __cdecl TwmDestroy(void* handle) {
    try { delete static_cast<Runtime*>(handle); }
    catch (...) { /* Never propagate exceptions across the C ABI. */ }
}
