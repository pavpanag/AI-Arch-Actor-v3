import torch

if torch.cuda.is_available():
    print("GPU is available and ready to use!")
    print(f"CUDA version: {torch.version.cuda}")
    print(f"Device name: {torch.cuda.get_device_name(0)}")
else:
    print("GPU is not available. Using CPU instead.")