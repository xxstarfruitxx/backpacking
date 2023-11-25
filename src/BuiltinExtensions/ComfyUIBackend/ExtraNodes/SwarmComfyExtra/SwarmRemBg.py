from PIL import Image
import numpy as np
import torch
from rembg import remove

class SwarmRemBg:
    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "images": ("IMAGE",),
            }
        }

    CATEGORY = "StableSwarmUI"
    RETURN_TYPES = ("IMAGE", "MASK",)
    FUNCTION = "rem"

    def rem(self, images):
        # TODO: Batch support?
        i = 255.0 * images[0].cpu().numpy()
        img = Image.fromarray(np.clip(i, 0, 255).astype(np.uint8))
        img = img.convert("RGBA")
        img = remove(img, post_process_mask=True)
        output = np.array(img).astype(np.float32) / 255.0
        output = torch.from_numpy(output)[None,]
        if 'A' in img.getbands():
            mask = np.array(img.getchannel('A')).astype(np.float32) / 255.0
            mask = 1. - torch.from_numpy(mask)
        else:
            mask = torch.zeros((64,64), dtype=torch.float32, device="cpu")
        return (output, mask.unsqueeze(0))

NODE_CLASS_MAPPINGS = {
    "SwarmRemBg": SwarmRemBg,
}
