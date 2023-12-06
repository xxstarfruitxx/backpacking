from . import SwarmBlending, SwarmClipSeg, SwarmImages, SwarmInternalUtil, SwarmKSampler, SwarmLoadImageB64, SwarmLoraLoader, SwarmMasks, SwarmSaveImageWS, SwarmTiling, SwarmExtractLora

WEB_DIRECTORY = "./web"

NODE_CLASS_MAPPINGS = (
    SwarmBlending.NODE_CLASS_MAPPINGS
    | SwarmClipSeg.NODE_CLASS_MAPPINGS
    | SwarmImages.NODE_CLASS_MAPPINGS
    | SwarmInternalUtil.NODE_CLASS_MAPPINGS
    | SwarmKSampler.NODE_CLASS_MAPPINGS
    | SwarmLoadImageB64.NODE_CLASS_MAPPINGS
    | SwarmLoraLoader.NODE_CLASS_MAPPINGS
    | SwarmMasks.NODE_CLASS_MAPPINGS
    | SwarmSaveImageWS.NODE_CLASS_MAPPINGS
    | SwarmTiling.NODE_CLASS_MAPPINGS
    | SwarmExtractLora.NODE_CLASS_MAPPINGS
)
