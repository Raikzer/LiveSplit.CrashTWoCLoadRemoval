"""Offline developer reference: stdout contains one confidence and nothing else."""
import argparse
import sys
from model import load_pixels, read_model, reference_predict


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model")
    parser.add_argument("image")
    args = parser.parse_args()
    try:
        confidence = reference_predict(read_model(args.model), load_pixels(args.image))
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    print(format(confidence, ".9g"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
