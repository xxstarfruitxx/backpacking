# Model Type Support In SwarmUI

Swarm natively supports [ModelSpec](https://github.com/Stability-AI/ModelSpec) metadata and can import metadata from some legacy formats used by other UIs (auto webui thumbnails, matrix jsons, etc)

Swarm supports models of all the common architectures:

### Stable Diffusion v1 and v2

SDv1/SDv2 models work exactly as normal. Even legacy (pre-[ModelSpec](https://github.com/Stability-AI/ModelSpec) models are supported).

### Stable Diffusion v1 Inpainting Models

SDv1 inpaint models (RunwayML) are supported, but will work best if you manually edit the Architecture ID to be `stable-diffusion-v1/inpaint`.

### Stable Diffusion XL

SDXL models work as normal, with the bonus that by default enhanced inference settings will be used (eg scaled up rescond).

Additional, SDXL-Refiner architecture models can be inferenced, both as refiner or even as a base (you must manually set res to 512x512 and it will generate weird results).

### Stable Diffusion 3

Stable Diffusion 3 Medium is supported and works as normal.

By default the first time you run an SD3 model, Swarm will download the text encoders for you.

Under the `Sampling` parameters group, a parameter named `SD3 TextEncs` is available to select whether to use CLIP, T5, or both. By default, CLIP is used (no T5) as results are near-identical but CLIP-only has much better performance, especially on systems with limited resources.

Under `Advanced Sampling`, the parameter `Sigma Shift` is available. This defaults to `3` on SD3, but you can lower it to around ~1.5 if you wish to experiment with different values. Messing with this value too much is not recommended.

For upscaling with SD3, the `Refiner Do Tiling` parameter is highly recommended (SD3 does not respond well to regular upscaling without tiling).

### SDXL Turbo and SD Turbo

Turbo models work the same as regular models, just set `CFG Scale` to `1` and `Steps` to `1` as well. Under the `Sampling` group set `Scheduler` to `Turbo`.

### Latency Consistency Models

LCM models work the same as regular models, just set `CFG Scale` to `1` and `Steps` to `4`. Under the `Sampling` group set `Sampler` to `lcm`.

### Lightning Models

Lightning models work the same as regular models, just set `CFG Scale` to `1` and (TODO: Sampling specifics for lightning).

### SegMind SSD-1B

SegMind SSD-1B models work the same as SD models.

### Stable Video Diffusion

SVD models are supported via the "Video" parameter group. Like XL, video by default uses enhanced inference settings (better sampler and larger sigma value).

You can do text2video by just checking Video as normal, or image2video by using an Init Image and setting Creativity to 0.

### Stable Cascade

Stable Cascade is supported if you use the "ComfyUI Format" models (aka "All In One") https://huggingface.co/stabilityai/stable-cascade/tree/main/comfyui_checkpoints that come as a pair of `stage_b` and `stage_c` models.

You must keep the two in the same folder, named the same with the only difference being `stage_b` vs `stage_c` in the filename.

Either model can be selected in the UI to use them, it will automatically use both.

### TensorRT

TensorRT support (`.engine`) is available for SDv1, SDv2-768-v, SDXL Base, SDXL Refiner, SD3-Medium

TensorRT is an nvidia-specific accelerator library that provides faster SD image generation at the cost of reduced flexibility. Generally this is best for heavy usages, especially for API/Bots/etc. and less useful for regular individual usage.

You can generate TensorRT engines from the model menu. This includes a button on-page to autoinstall TRT support your first time using it, and configuration of graph size limits and optimal scales. (TensorRT works fastest when you generate at the selected optimal resolution, and slightly less fast at any dynamic resolution outside the optimal setting.)

Note that TensorRT is not compatible with LoRAs, ControlNets, etc.

Note that you need to make a fresh TRT engine for any different model you want to use.

# PixArt Sigma

The [PixArt Sigma MS models](https://huggingface.co/PixArt-alpha/PixArt-Sigma/tree/main) are supported in Swarm with a few setup steps.

These steps are not friendly to beginners (if PixArt gains popularity, likely more direct/automated/native support will be added), but advanced users can follow:

- You must install https://github.com/city96/ComfyUI_ExtraModels to your Comfy backend.
- After downloading the model, run Swarm's **Utilities** -> **Pickle To Safetensors** -> `Convert Models`. You need a safetensors models for Swarm to accurately identify model type.
- After you have a safetensors model, find it in the Models tab and click the menu button on the model and select "`Edit Metadata`"
    - From the `Architecture` dropdown, select `PixArtMS Sigma XL 2` for 1024 or lower models, or `XL 2 (2K)` for the 2k
    - In the `Standard Resolution` box, enter `1024x1024` for 1024 or `512x512` for the 512, or `2048x2048` for the 2k
- Make sure in **User Settings**, you have a `DefaultSDXLVae` selected. If not, you can download this one https://huggingface.co/madebyollin/sdxl-vae-fp16-fix and save it in `(Swarm)/Models/VAE`
- Swarm will autodownload T5XXL-EncoderOnly for you on first run (same as SD3-Medium T5-Only mode)
- You can now use the model as easily as any other model. Some feature compatibility features might arise.

# AuraFlow v0.1

[Fal.ai's AuraFlow v0.1](https://huggingface.co/fal/AuraFlow/tree/main) is supported in Swarm, but you must manually select architecture to use it. (The AuraFlow team intend to add modelspec metadata in the near future).

Download the model, then click "`Edit Metadata`" and select `(Temporary) AuraFlow` as the architecture, and set resolution to `1024x1024`.
