﻿using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using System.IO;

namespace SwarmUI.Builtin_ComfyUIBackend;

/// <summary>Helper class for generating ComfyUI workflows from input parameters.</summary>
public class WorkflowGenerator
{
    /// <summary>Represents a step in the workflow generation process.</summary>
    /// <param name="Action">The action to take.</param>
    /// <param name="Priority">The priority to apply it at.
    /// These are such from lowest to highest.
    /// "-10" is the priority of the first core pre-init,
    /// "0" is before final outputs,
    /// "10" is final output.</param>
    public record class WorkflowGenStep(Action<WorkflowGenerator> Action, double Priority);

    /// <summary>Callable steps for modifying workflows as they go.</summary>
    public static List<WorkflowGenStep> Steps = [];

    /// <summary>Callable steps for configuring model generation.</summary>
    public static List<WorkflowGenStep> ModelGenSteps = [];

    /// <summary>Can be set to globally block custom nodes, if needed.</summary>
    public static volatile bool RestrictCustomNodes = false;

    /// <summary>Supported Features of the comfy backend.</summary>
    public HashSet<string> Features = [];

    /// <summary>Helper tracker for CLIP Models that are loaded (to skip a datadrive read from being reused every time).</summary>
    public static HashSet<string> ClipModelsValid = [];

    /// <summary>Helper tracker for Vision Models that are loaded (to skip a datadrive read from being reused every time).</summary>
    public static HashSet<string> VisionModelsValid = [];

    /// <summary>Helper tracker for IP Adapter Models that are loaded (to skip a datadrive read from being reused every time).</summary>
    public static HashSet<string> IPAdapterModelsValid = [];

    /// <summary>Register a new step to the workflow generator.</summary>
    public static void AddStep(Action<WorkflowGenerator> step, double priority)
    {
        Steps.Add(new(step, priority));
        Steps = [.. Steps.OrderBy(s => s.Priority)];
    }

    /// <summary>Register a new step to the workflow generator.</summary>
    public static void AddModelGenStep(Action<WorkflowGenerator> step, double priority)
    {
        ModelGenSteps.Add(new(step, priority));
        ModelGenSteps = [.. ModelGenSteps.OrderBy(s => s.Priority)];
    }

    static WorkflowGenerator()
    {
        WorkflowGeneratorSteps.Register();
    }

    /// <summary>Lock for when ensuring the backend has valid models.</summary>
    public static MultiLockSet<string> ModelDownloaderLocks = new(32);

    /// <summary>The raw user input data.</summary>
    public T2IParamInput UserInput;

    /// <summary>The output workflow object.</summary>
    public JObject Workflow;

    /// <summary>Lastmost node ID for key input trackers.</summary>
    public JArray FinalModel = ["4", 0],
        FinalClip = ["4", 1],
        FinalInputImage = null,
        FinalMask = null,
        FinalVae = ["4", 2],
        FinalLatentImage = ["5", 0],
        FinalPrompt = ["6", 0],
        FinalNegativePrompt = ["7", 0],
        FinalSamples = ["10", 0],
        FinalImageOut = null,
        LoadingModel = null, LoadingClip = null, LoadingVAE = null;

    /// <summary>If true, something has required the workflow stop now.</summary>
    public bool SkipFurtherSteps = false;

    /// <summary>What model currently matches <see cref="FinalModel"/>.</summary>
    public T2IModel FinalLoadedModel;

    /// <summary>Mapping of any extra nodes to keep track of, Name->ID, eg "MyNode" -> "15".</summary>
    public Dictionary<string, string> NodeHelpers = [];

    /// <summary>Last used ID, tracked to safely add new nodes with sequential IDs. Note that this starts at 100, as below 100 is reserved for constant node IDs.</summary>
    public int LastID = 100;

    /// <summary>Model folder separator format, if known.</summary>
    public string ModelFolderFormat;

    /// <summary>Type id ('Base', 'Refiner') of the current loading model.</summary>
    public string LoadingModelType;

    /// <summary>If true, user-selected VAE may be wrong, so ignore it.</summary>
    public bool NoVAEOverride = false;

    /// <summary>If true, the generator is currently working on the refiner stage.</summary>
    public bool IsRefinerStage = false;

    /// <summary>If true, the main sampler should add noise. If false, it shouldn't.</summary>
    public bool MainSamplerAddNoise = true;

    /// <summary>If true, Differential Diffusion node has been attached to the current model.</summary>
    public bool IsDifferentialDiffusion = false;

    /// <summary>Outputs of <see cref="CreateImageMaskCrop(JArray, JArray, int)"/> if used for the main image.</summary>
    public (string, string, string) MaskShrunkInfo = (null, null, null);

    /// <summary>Gets the current loaded model class.</summary>
    public T2IModelClass CurrentModelClass()
    {
        FinalLoadedModel ??= UserInput.Get(T2IParamTypes.Model, null);
        return FinalLoadedModel?.ModelClass;
    }

    /// <summary>Gets the current loaded model compat class.</summary>
    public string CurrentCompatClass()
    {
        return CurrentModelClass()?.CompatClass;
    }

    /// <summary>Returns true if the current model is Stable Cascade.</summary>
    public bool IsCascade()
    {
        string clazz = CurrentCompatClass();
        return clazz is not null && clazz == "stable-cascade-v1";
    }

    /// <summary>Returns true if the current model is Stable Diffusion 3.</summary>
    public bool IsSD3()
    {
        string clazz = CurrentCompatClass();
        return clazz is not null && clazz == "stable-diffusion-v3-medium";
    }

    /// <summary>Gets a dynamic ID within a semi-stable registration set.</summary>
    public string GetStableDynamicID(int index, int offset)
    {
        int id = 1000 + index + offset;
        string result = $"{id}";
        if (HasNode(result))
        {
            return GetStableDynamicID(index, offset + 1);
        }
        return result;
    }

    /// <summary>Creates a new node with the given class type and configuration action, and optional manual ID.</summary>
    public string CreateNode(string classType, Action<string, JObject> configure, string id = null)
    {
        id ??= $"{LastID++}";
        JObject obj = new() { ["class_type"] = classType };
        configure(id, obj);
        Workflow[id] = obj;
        return id;
    }

    /// <summary>Creates a new node with the given class type and input data, and optional manual ID.</summary>
    public string CreateNode(string classType, JObject input, string id = null)
    {
        return CreateNode(classType, (_, n) => n["inputs"] = input, id);
    }

    /// <summary>Helper to download a core model file required by the workflow.</summary>
    public void DownloadModel(string name, string filePath, string url)
    {
        if (File.Exists(filePath))
        {
            return;
        }
        lock (ModelDownloaderLocks.GetLock(name))
        {
            if (File.Exists(filePath)) // Double-check in case another thread downloaded it
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            Logs.Info($"Downloading {name} to {filePath}...");
            double nextPerc = 0.05;
            try
            {
                Utilities.DownloadFile(url, filePath, (bytes, total, perSec) =>
                {
                    double perc = bytes / (double)total;
                    if (perc >= nextPerc)
                    {
                        Logs.Info($"{name} download at {perc * 100:0.0}%...");
                        // TODO: Send a signal back so a progress bar can be displayed on a UI
                        nextPerc = Math.Round(perc / 0.05) * 0.05 + 0.05;
                    }
                }).Wait();
            }
            catch (Exception ex)
            {
                Logs.Error($"Failed to download {name} from {url}: {ex.Message}");
                File.Delete(filePath);
                throw new InvalidOperationException("Required model download failed.");
            }
            Logs.Info($"Downloading complete, continuing.");
        }
    }

    /// <summary>Loads and applies LoRAs in the user parameters for the given LoRA confinement ID.</summary>
    public (JArray, JArray) LoadLorasForConfinement(int confinement, JArray model, JArray clip)
    {
        if (confinement < 0 || !UserInput.TryGet(T2IParamTypes.Loras, out List<string> loras))
        {
            return (model, clip);
        }
        List<string> weights = UserInput.Get(T2IParamTypes.LoraWeights);
        List<string> confinements = UserInput.Get(T2IParamTypes.LoraSectionConfinement);
        if (confinement > 0 && (confinements is null || confinements.Count == 0))
        {
            return (model, clip);
        }
        T2IModelHandler loraHandler = Program.T2IModelSets["LoRA"];
        for (int i = 0; i < loras.Count; i++)
        {
            if (!loraHandler.Models.TryGetValue(loras[i] + ".safetensors", out T2IModel lora))
            {
                if (!loraHandler.Models.TryGetValue(loras[i], out lora))
                {
                    throw new InvalidDataException($"LoRA Model '{loras[i]}' not found in the model set.");
                }
            }
            if (confinements is not null && confinements.Count > i)
            {
                int confinementId = int.Parse(confinements[i]);
                if (confinementId >= 0 && confinementId != confinement)
                {
                    continue;
                }
            }
            float weight = weights == null ? 1 : float.Parse(weights[i]);
            string newId = CreateNode("LoraLoader", new JObject()
            {
                ["model"] = model,
                ["clip"] = clip,
                ["lora_name"] = lora.ToString(ModelFolderFormat),
                ["strength_model"] = weight,
                ["strength_clip"] = weight
            }, GetStableDynamicID(500, i));
            model = [newId, 0];
            clip = [newId, 1];
        }
        return (model, clip);
    }

    /// <summary>Creates a new node to load an image.</summary>
    public string CreateLoadImageNode(Image img, string param, bool resize, string nodeId = null)
    {
        if (nodeId is null && NodeHelpers.TryGetValue($"imgloader_{param}_{resize}", out string alreadyLoaded))
        {
            return alreadyLoaded;
        }
        string result;
        if (Features.Contains("comfy_loadimage_b64") && !RestrictCustomNodes)
        {
            result = CreateNode("SwarmLoadImageB64", new JObject()
            {
                ["image_base64"] = (resize ? img.Resize(UserInput.Get(T2IParamTypes.Width), UserInput.GetImageHeight()) : img).AsBase64
            }, nodeId);
        }
        else
        {
            result = CreateNode("LoadImage", new JObject()
            {
                ["image"] = param
            }, nodeId);
        }
        NodeHelpers[$"imgloader_{param}_{resize}"] = result;
        return result;
    }

    /// <summary>Creates an automatic image mask-crop before sampling, to be followed by <see cref="RecompositeCropped(string, string, JArray, JArray)"/> after sampling.</summary>
    /// <param name="mask">The mask node input.</param>
    /// <param name="image">The image node input.</param>
    /// <param name="growBy">Number of pixels to grow the boundary by.</param>
    /// <param name="vae">The relevant VAE.</param>
    /// <param name="model">The model in use, for determining resolution.</param>
    /// <param name="threshold">Optional minimum value threshold.</param>
    /// <param name="thresholdMax">Optional maximum value of the threshold.</param>
    /// <returns>(boundsNode, croppedMask, maskedLatent).</returns>
    public (string, string, string) CreateImageMaskCrop(JArray mask, JArray image, int growBy, JArray vae, T2IModel model, double threshold = 0.01, double thresholdMax = 1)
    {
        if (threshold > 0)
        {
            string thresholded = CreateNode("SwarmMaskThreshold", new JObject()
            {
                ["mask"] = mask,
                ["min"] = threshold,
                ["max"] = thresholdMax
            });
            mask = [thresholded, 0];
        }
        string boundsNode = CreateNode("SwarmMaskBounds", new JObject()
        {
            ["mask"] = mask,
            ["grow"] = growBy
        });
        string croppedImage = CreateNode("SwarmImageCrop", new JObject()
        {
            ["image"] = image,
            ["x"] = new JArray() { boundsNode, 0 },
            ["y"] = new JArray() { boundsNode, 1 },
            ["width"] = new JArray() { boundsNode, 2 },
            ["height"] = new JArray() { boundsNode, 3 }
        });
        string croppedMask = CreateNode("CropMask", new JObject()
        {
            ["mask"] = mask,
            ["x"] = new JArray() { boundsNode, 0 },
            ["y"] = new JArray() { boundsNode, 1 },
            ["width"] = new JArray() { boundsNode, 2 },
            ["height"] = new JArray() { boundsNode, 3 }
        });
        string scaledImage = CreateNode("SwarmImageScaleForMP", new JObject()
        {
            ["image"] = new JArray() { croppedImage, 0 },
            ["width"] = model?.StandardWidth <= 0 ? UserInput.Get(T2IParamTypes.Width, 1024) : model.StandardWidth,
            ["height"] = model?.StandardHeight <= 0 ? UserInput.GetImageHeight() : model.StandardHeight,
            ["can_shrink"] = true
        });
        string vaeEncoded = CreateVAEEncode(vae, [scaledImage, 0], null, true);
        string masked = CreateNode("SetLatentNoiseMask", new JObject()
        {
            ["samples"] = new JArray() { vaeEncoded, 0 },
            ["mask"] = new JArray() { croppedMask, 0 }
        });
        return (boundsNode, croppedMask, masked);
    }

    /// <summary>Returns a masked image composite with mask thresholding.</summary>
    public JArray CompositeMask(JArray baseImage, JArray newImage, JArray mask)
    {
        if (!UserInput.Get(T2IParamTypes.MaskCompositeUnthresholded, false))
        {
            string thresholded = CreateNode("ThresholdMask", new JObject()
            {
                ["mask"] = mask,
                ["value"] = 0.001
            });
            mask = [thresholded, 0];
        }
        string composited = CreateNode("ImageCompositeMasked", new JObject()
        {
            ["destination"] = baseImage,
            ["source"] = newImage,
            ["mask"] = mask,
            ["x"] = 0,
            ["y"] = 0,
            ["resize_source"] = false
        });
        return [composited, 0];
    }

    /// <summary>Recomposites a masked image edit, after <see cref="CreateImageMaskCrop(JArray, JArray, int)"/> was used.</summary>
    public JArray RecompositeCropped(string boundsNode, JArray croppedMask, JArray firstImage, JArray newImage)
    {
        string scaledBack = CreateNode("ImageScale", new JObject()
        {
            ["image"] = newImage,
            ["width"] = new JArray() { boundsNode, 2 },
            ["height"] = new JArray() { boundsNode, 3 },
            ["upscale_method"] = "bilinear",
            ["crop"] = "disabled"
        });
        if (!UserInput.Get(T2IParamTypes.MaskCompositeUnthresholded, false))
        {
            string thresholded = CreateNode("ThresholdMask", new JObject()
            {
                ["mask"] = croppedMask,
                ["value"] = 0.001
            });
            croppedMask = [thresholded, 0];
        }
        string composited = CreateNode("ImageCompositeMasked", new JObject()
        {
            ["destination"] = firstImage,
            ["source"] = new JArray() { scaledBack, 0 },
            ["mask"] = croppedMask,
            ["x"] = new JArray() { boundsNode, 0 },
            ["y"] = new JArray() { boundsNode, 1 },
            ["resize_source"] = false
        });
        return [composited, 0];
    }

    /// <summary>Call to run the generation process and get the result.</summary>
    public JObject Generate()
    {
        Workflow = [];
        foreach (WorkflowGenStep step in Steps)
        {
            step.Action(this);
            if (SkipFurtherSteps)
            {
                break;
            }
        }
        return Workflow;
    }

    /// <summary>Returns true if the given node ID has already been used.</summary>
    public bool HasNode(string id)
    {
        return Workflow.ContainsKey(id);
    }

    /// <summary>Creates a node to save an image output.</summary>
    public string CreateImageSaveNode(JArray image, string id = null)
    {
        if (Features.Contains("comfy_saveimage_ws") && !RestrictCustomNodes)
        {
            return CreateNode("SwarmSaveImageWS", new JObject()
            {
                ["images"] = image
            }, id);
        }
        else
        {
            return CreateNode("SaveImage", new JObject()
            {
                ["filename_prefix"] = $"SwarmUI_{Random.Shared.Next():X4}_",
                ["images"] = image
            }, id);
        }
    }

    /// <summary>Creates a model loader and adapts it with any registered model adapters, and returns (Model, Clip, VAE).</summary>
    public (T2IModel, JArray, JArray, JArray) CreateStandardModelLoader(T2IModel model, string type, string id = null, bool noCascadeFix = false)
    {
        void requireClipModel(string name, string url)
        {
            if (ClipModelsValid.Contains(name))
            {
                return;
            }
            string filePath = Utilities.CombinePathWithAbsolute(Program.ServerSettings.Paths.ModelRoot, "clip", name);
            DownloadModel(name, filePath, url);
            ClipModelsValid.Add(name);
        }
        IsDifferentialDiffusion = false;
        LoadingModelType = type;
        if (!noCascadeFix && model.ModelClass?.ID == "stable-cascade-v1-stage-b" && model.Name.Contains("stage_b") && Program.MainSDModels.Models.TryGetValue(model.Name.Replace("stage_b", "stage_c"), out T2IModel altCascadeModel))
        {
            model = altCascadeModel;
        }
        if (model.ModelClass?.ID.EndsWith("/tensorrt") ?? false)
        {
            string baseArch = model.ModelClass?.ID?.Before('/');
            string trtType = ComfyUIWebAPI.ArchitecturesTRTCompat[baseArch];
            string trtloader = CreateNode("TensorRTLoader", new JObject()
            {
                ["unet_name"] = model.ToString(ModelFolderFormat),
                ["model_type"] = trtType
            }, id);
            LoadingModel = [trtloader, 0];
            // TODO: This is a hack
            T2IModel[] sameArch = [.. Program.MainSDModels.Models.Values.Where(m => m.ModelClass?.ID == baseArch)];
            if (sameArch.Length == 0)
            {
                throw new InvalidDataException($"No models found with architecture {baseArch}, cannot load CLIP/VAE for this Arch");
            }
            T2IModel matchedName = sameArch.FirstOrDefault(m => m.Name.Before('.') == model.Name.Before('.'));
            matchedName ??= sameArch.First();
            string secondaryNode = CreateNode("CheckpointLoaderSimple", new JObject()
            {
                ["ckpt_name"] = matchedName.ToString(ModelFolderFormat)
            });
            LoadingClip = [secondaryNode, 1];
            LoadingVAE = [secondaryNode, 2];
        }
        else if (model.Name.EndsWith(".engine"))
        {
            throw new InvalidDataException($"Model {model.Name} appears to be TensorRT lacks metadata to identify its architecture, cannot load");
        }
        else if (model.ModelClass?.CompatClass == "pixart-ms-sigma-xl-2")
        {
            string pixartNode = CreateNode("PixArtCheckpointLoader", new JObject()
            {
                ["ckpt_name"] = model.ToString(ModelFolderFormat),
                ["model"] = model.ModelClass.ID == "pixart-ms-sigma-xl-2-2k" ? "PixArtMS_Sigma_XL_2_2K" : "PixArtMS_Sigma_XL_2"
            }, id);
            LoadingModel = [pixartNode, 0];
            requireClipModel("t5xxl_enconly.safetensors", "https://huggingface.co/mcmonkey/google_t5-v1_1-xxl_encoderonly/resolve/main/t5xxl_fp8_e4m3fn.safetensors");
            string singleClipLoader = CreateNode("CLIPLoader", new JObject()
            {
                ["clip_name"] = "t5xxl_enconly.safetensors",
                ["type"] = "sd3"
            });
            LoadingClip = [singleClipLoader, 0];
            string xlVae = UserInput.SourceSession?.User?.Settings?.VAEs?.DefaultSDXLVAE;
            if (string.IsNullOrWhiteSpace(xlVae))
            {
                xlVae = Program.T2IModelSets["VAE"].Models.Keys.FirstOrDefault(m => m.ToLowerFast().Contains("sdxl"));
            }
            if (string.IsNullOrWhiteSpace(xlVae))
            {
                throw new InvalidDataException("No default SDXL VAE found, please download an SDXL VAE and set it as default in User Settings");
            }
            string vaeLoader = CreateNode("VAELoader", new JObject()
            {
                ["vae_name"] = xlVae
            });
            LoadingVAE = [vaeLoader, 0];
        }
        else
        {
            string modelNode = CreateNode("CheckpointLoaderSimple", new JObject()
            {
                ["ckpt_name"] = model.ToString(ModelFolderFormat)
            }, id);
            LoadingModel = [modelNode, 0];
            LoadingClip = [modelNode, 1];
            LoadingVAE = [modelNode, 2];
        }
        string predType = model.Metadata?.PredictionType;
        if (CurrentCompatClass() == "stable-diffusion-v3-medium")
        {
            string sd3Node = CreateNode("ModelSamplingSD3", new JObject()
            {
                ["model"] = LoadingModel,
                ["shift"] = UserInput.Get(T2IParamTypes.SigmaShift, 3)
            });
            LoadingModel = [sd3Node, 0];
            string tencs = model.Metadata?.TextEncoders ?? "";
            if (!UserInput.TryGet(T2IParamTypes.SD3TextEncs, out string mode))
            {
                if (tencs == "")
                {
                    mode = "CLIP Only";
                }
                else
                {
                    mode = null;
                }
            }
            if (mode == "CLIP Only" && tencs.Contains("clip_l") && !tencs.Contains("t5xxl")) { mode = null; }
            if (mode == "T5 Only" && !tencs.Contains("clip_l") && tencs.Contains("t5xxl")) { mode = null; }
            if (mode == "CLIP + T5" && tencs.Contains("clip_l") && tencs.Contains("t5xxl")) { mode = null; }
            if (mode is not null)
            {
                requireClipModel("clip_g_sdxl_base.safetensors", "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder_2/model.fp16.safetensors");
                requireClipModel("clip_l_sdxl_base.safetensors", "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/text_encoder/model.fp16.safetensors");
                if (mode.Contains("T5"))
                {
                    requireClipModel("t5xxl_enconly.safetensors", "https://huggingface.co/mcmonkey/google_t5-v1_1-xxl_encoderonly/resolve/main/t5xxl_fp8_e4m3fn.safetensors");
                }
                if (mode == "T5 Only")
                {
                    string singleClipLoader = CreateNode("CLIPLoader", new JObject()
                    {
                        ["clip_name"] = "t5xxl_enconly.safetensors",
                        ["type"] = "sd3"
                    });
                    LoadingClip = [singleClipLoader, 0];
                }
                else if (mode == "CLIP Only")
                {
                    string dualClipLoader = CreateNode("DualCLIPLoader", new JObject()
                    {
                        ["clip_name1"] = "clip_g_sdxl_base.safetensors",
                        ["clip_name2"] = "clip_l_sdxl_base.safetensors",
                        ["type"] = "sd3"
                    });
                    LoadingClip = [dualClipLoader, 0];
                }
                else
                {
                    string tripleClipLoader = CreateNode("TripleCLIPLoader", new JObject()
                    {
                        ["clip_name1"] = "clip_g_sdxl_base.safetensors",
                        ["clip_name2"] = "clip_l_sdxl_base.safetensors",
                        ["clip_name3"] = "t5xxl_enconly.safetensors"
                    });
                    LoadingClip = [tripleClipLoader, 0];
                }
            }
        }
        else if (CurrentCompatClass() == "auraflow-v1")
        {
            string sd3Node = CreateNode("ModelSamplingAuraFlow", new JObject()
            {
                ["model"] = LoadingModel,
                ["shift"] = UserInput.Get(T2IParamTypes.SigmaShift, 1.73)
            });
        }
        else if (!string.IsNullOrWhiteSpace(predType))
        {
            string discreteNode = CreateNode("ModelSamplingDiscrete", new JObject()
            {
                ["model"] = LoadingModel,
                ["sampling"] = predType switch { "v" => "v_prediction", "v-zsnr" => "v_prediction", "epsilon" => "eps", _ => predType },
                ["zsnr"] = predType.Contains("zsnr")
            });
            LoadingModel = [discreteNode, 0];
        }
        foreach (WorkflowGenStep step in ModelGenSteps)
        {
            step.Action(this);
        }
        return (model, LoadingModel, LoadingClip, LoadingVAE);
    }

    /// <summary>Creates a VAEDecode node and returns its node ID.</summary>
    public string CreateVAEDecode(JArray vae, JArray latent, string id = null)
    {
        if (UserInput.TryGet(T2IParamTypes.VAETileSize, out int tileSize))
        {
            return CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = vae,
                ["samples"] = latent,
                ["tile_size"] = tileSize
            }, id);
        }
        return CreateNode("VAEDecode", new JObject()
        {
            ["vae"] = vae,
            ["samples"] = latent
        }, id);
    }

    /// <summary>Default sampler type.</summary>
    public string DefaultSampler = "euler";

    /// <summary>Default sampler scheduler type.</summary>
    public string DefaultScheduler = "normal";

    /// <summary>Default previews type.</summary>
    public string DefaultPreviews = "default";

    /// <summary>Creates a KSampler and returns its node ID.</summary>
    public string CreateKSampler(JArray model, JArray pos, JArray neg, JArray latent, double cfg, int steps, int startStep, int endStep, long seed, bool returnWithLeftoverNoise, bool addNoise, double sigmin = -1, double sigmax = -1, string previews = null, string defsampler = null, string defscheduler = null, string id = null, bool rawSampler = false, bool doTiled = false)
    {
        bool willCascadeFix = false;
        JArray cascadeModel = null;
        if (!rawSampler && IsCascade() && FinalLoadedModel.Name.Contains("stage_c") && Program.MainSDModels.Models.TryGetValue(FinalLoadedModel.Name.Replace("stage_c", "stage_b"), out T2IModel bModel))
        {
            (_, cascadeModel, _, FinalVae) = CreateStandardModelLoader(bModel, LoadingModelType, null, true);
            willCascadeFix = true;
            defsampler ??= "euler_ancestral";
            defscheduler ??= "simple";
        }
        if (FinalLoadedModel?.ModelClass ?.ID == "stable-diffusion-xl-v1-edit")
        {
            // TODO: SamplerCustomAdvanced logic should be used for *all* models, not just ip2p
            if (FinalInputImage is null)
            {
                // TODO: Get the correct image (eg if edit is used as a refiner or something silly it should still work)
                string decoded = CreateVAEDecode(FinalVae, latent);
                FinalInputImage = [decoded, 0];
            }
            string ip2p2condNode = CreateNode("InstructPixToPixConditioning", new JObject()
            {
                ["positive"] = pos,
                ["negative"] = neg,
                ["vae"] = FinalVae,
                ["pixels"] = FinalInputImage
            });
            string noiseNode = CreateNode("RandomNoise", new JObject()
            {
                ["noise_seed"] = seed
            });
            // TODO: VarSeed, batching, etc. seed logic
            string cfgGuiderNode = CreateNode("DualCFGGuider", new JObject()
            {
                ["model"] = model,
                ["cond1"] = new JArray() { ip2p2condNode, 0 },
                ["cond2"] = new JArray() { ip2p2condNode, 1 },
                ["negative"] = neg,
                ["cfg_conds"] = cfg,
                ["cfg_cond2_negative"] = UserInput.Get(T2IParamTypes.IP2PCFG2, 1.5)
            });
            string samplerNode = CreateNode("KSamplerSelect", new JObject()
            {
                ["sampler_name"] = UserInput.Get(ComfyUIBackendExtension.SamplerParam, defsampler ?? DefaultSampler)
            });
            string scheduler = UserInput.Get(ComfyUIBackendExtension.SchedulerParam, defscheduler ?? DefaultScheduler).ToLowerFast();
            double denoise = 1;// 1.0 - (startStep / (double)steps); // NOTE: Edit model breaks on denoise<1
            JArray schedulerNode;
            if (scheduler == "turbo")
            {
                string turboNode = CreateNode("SDTurboScheduler", new JObject()
                {
                    ["model"] = model,
                    ["steps"] = steps,
                    ["denoise"] = denoise
                });
                schedulerNode = [turboNode, 0];
            }
            else if (scheduler == "karras")
            {
                string karrasNode = CreateNode("KarrasScheduler", new JObject()
                {
                    ["steps"] = steps,
                    ["sigma_max"] = sigmax <= 0 ? 14.614642 : sigmax,
                    ["sigma_min"] = sigmin <= 0 ? 0.0291675 : sigmin,
                    ["rho"] = UserInput.Get(T2IParamTypes.SamplerRho, 7)
                });
                schedulerNode = [karrasNode, 0];
                if (startStep > 0)
                {
                    string afterStart = CreateNode("SplitSigmas", new JObject()
                    {
                        ["sigmas"] = schedulerNode,
                        ["step"] = startStep
                    });
                    schedulerNode = [afterStart, 1];
                }
            }
            else
            {
                string basicNode = CreateNode("BasicScheduler", new JObject()
                {
                    ["model"] = model,
                    ["steps"] = steps,
                    ["scheduler"] = scheduler,
                    ["denoise"] = denoise
                });
                schedulerNode = [basicNode, 0];
            }
            if (endStep < steps)
            {
                string beforeEnd = CreateNode("SplitSigmas", new JObject()
                {
                    ["sigmas"] = schedulerNode,
                    ["step"] = endStep
                });
                schedulerNode = [beforeEnd, 0];
            }
            string finalSampler = CreateNode("SamplerCustomAdvanced", new JObject()
            {
                ["sampler"] = new JArray() { samplerNode, 0 },
                ["guider"] = new JArray() { cfgGuiderNode, 0 },
                ["sigmas"] = schedulerNode,
                ["latent_image"] = new JArray() { ip2p2condNode, 2 },
                ["noise"] = new JArray() { noiseNode, 0 }
            }, id);
            return finalSampler;
        }
        string firstId = willCascadeFix ? null : id;
        JObject inputs = new()
        {
            ["model"] = model,
            ["noise_seed"] = seed,
            ["steps"] = steps,
            ["cfg"] = cfg,
            // TODO: proper sampler input, and intelligent default scheduler per sampler
            ["sampler_name"] = UserInput.Get(ComfyUIBackendExtension.SamplerParam, defsampler ?? DefaultSampler),
            ["scheduler"] = UserInput.Get(ComfyUIBackendExtension.SchedulerParam, defscheduler ?? DefaultScheduler),
            ["positive"] = pos,
            ["negative"] = neg,
            ["latent_image"] = latent,
            ["start_at_step"] = startStep,
            ["end_at_step"] = endStep,
            ["return_with_leftover_noise"] = returnWithLeftoverNoise ? "enable" : "disable",
            ["add_noise"] = addNoise ? "enable" : "disable"
        };
        string created;
        if (Features.Contains("variation_seed") && !RestrictCustomNodes)
        {
            inputs["var_seed"] = UserInput.Get(T2IParamTypes.VariationSeed, 0);
            inputs["var_seed_strength"] = UserInput.Get(T2IParamTypes.VariationSeedStrength, 0);
            inputs["sigma_min"] = UserInput.Get(T2IParamTypes.SamplerSigmaMin, sigmin);
            inputs["sigma_max"] = UserInput.Get(T2IParamTypes.SamplerSigmaMax, sigmax);
            inputs["rho"] = UserInput.Get(T2IParamTypes.SamplerRho, 7);
            inputs["previews"] = UserInput.Get(T2IParamTypes.NoPreviews) ? "none" : previews ?? DefaultPreviews;
            inputs["tile_sample"] = doTiled;
            inputs["tile_size"] = FinalLoadedModel.StandardWidth <= 0 ? 768 : FinalLoadedModel.StandardWidth;
            created = CreateNode("SwarmKSampler", inputs, firstId);
        }
        else
        {
            created = CreateNode("KSamplerAdvanced", inputs, firstId);
        }
        if (willCascadeFix)
        {
            string stageBCond = CreateNode("StableCascade_StageB_Conditioning", new JObject()
            {
                ["stage_c"] = new JArray() { created, 0 },
                ["conditioning"] = pos
            });
            created = CreateKSampler(cascadeModel, [stageBCond, 0], neg, [latent[0], 1], 1.1, steps, startStep, endStep, seed + 27, returnWithLeftoverNoise, addNoise, sigmin, sigmax, previews ?? previews, defsampler, defscheduler, id, true);
        }
        return created;
    }

    /// <summary>Creates a VAE Encode node.</summary>
    public string CreateVAEEncode(JArray vae, JArray image, string id = null, bool noCascade = false, JArray mask = null)
    {
        if (!noCascade && IsCascade())
        {
            return CreateNode("StableCascade_StageC_VAEEncode", new JObject()
            {
                ["vae"] = vae,
                ["image"] = image,
                ["compression"] = UserInput.Get(T2IParamTypes.CascadeLatentCompression, 32)
            }, id);
        }
        else
        {
            if (mask is not null && (UserInput.Get(T2IParamTypes.UseInpaintingEncode) || (CurrentModelClass()?.ID ?? "").EndsWith("/inpaint")))
            {
                return CreateNode("VAEEncodeForInpaint", new JObject()
                {
                    ["vae"] = vae,
                    ["pixels"] = image,
                    ["mask"] = mask,
                    ["grow_mask_by"] = 6
                }, id);
            }
            return CreateNode("VAEEncode", new JObject()
            {
                ["vae"] = vae,
                ["pixels"] = image
            }, id);
        }
    }

    /// <summary>Creates an Empty Latent Image node.</summary>
    public string CreateEmptyImage(int width, int height, int batchSize, string id = null)
    {
        if (IsCascade())
        {
            return CreateNode("StableCascade_EmptyLatentImage", new JObject()
            {
                ["batch_size"] = batchSize,
                ["compression"] = UserInput.Get(T2IParamTypes.CascadeLatentCompression, 32),
                ["height"] = height,
                ["width"] = width
            }, id);
        }
        else if (IsSD3())
        {
            return CreateNode("EmptySD3LatentImage", new JObject()
            {
                ["batch_size"] = batchSize,
                ["height"] = height,
                ["width"] = width
            }, id);
        }
        else if (UserInput.Get(ComfyUIBackendExtension.ShiftedLatentAverageInit, false))
        {
            double offA = 0, offB = 0, offC = 0, offD = 0;
            switch (FinalLoadedModel.ModelClass?.CompatClass)
            {
                case "stable-diffusion-v1": // https://github.com/Birch-san/sdxl-diffusion-decoder/blob/4ba89847c02db070b766969c0eca3686a1e7512e/script/inference_decoder.py#L112
                case "stable-diffusion-v2":
                    offA = 2.1335;
                    offB = 0.1237;
                    offC = 0.4052;
                    offD = -0.0940;
                    break;
                case "stable-diffusion-xl-v1": // https://huggingface.co/datasets/Birchlabs/sdxl-latents-ffhq
                    offA = -2.8982;
                    offB = -0.9609;
                    offC = 0.2416;
                    offD = -0.3074;
                    break;
            }
            return CreateNode("SwarmOffsetEmptyLatentImage", new JObject()
            {
                ["batch_size"] = batchSize,
                ["height"] = height,
                ["width"] = width,
                ["off_a"] = offA,
                ["off_b"] = offB,
                ["off_c"] = offC,
                ["off_d"] = offD
            }, id);
        }
        else
        {
            return CreateNode("EmptyLatentImage", new JObject()
            {
                ["batch_size"] = batchSize,
                ["height"] = height,
                ["width"] = width
            }, id);
        }
    }

    /// <summary>Enables Differential Diffusion on the current model if is enabled in user settings.</summary>
    public void EnableDifferential()
    {
        if (IsDifferentialDiffusion || UserInput.Get(T2IParamTypes.MaskBehavior, "Differential") != "Differential")
        {
            return;
        }
        IsDifferentialDiffusion = true;
        string diffNode = CreateNode("DifferentialDiffusion", new JObject()
        {
            ["model"] = FinalModel
        });
        FinalModel = [diffNode, 0];
    }

    /// <summary>Creates a "CLIPTextEncode" or equivalent node for the given input.</summary>
    public JArray CreateConditioningDirect(string prompt, JArray clip, T2IModel model, bool isPositive, string id = null)
    {
        string node;
        double mult = isPositive ? 1.5 : 0.8;
        int width = UserInput.Get(T2IParamTypes.Width, 1024);
        int height = UserInput.GetImageHeight();
        bool enhance = UserInput.Get(T2IParamTypes.ModelSpecificEnhancements, true);
        if (Features.Contains("variation_seed") && prompt.Contains('[') && prompt.Contains(']'))
        {
            node = CreateNode("SwarmClipTextEncodeAdvanced", new JObject()
            {
                ["clip"] = clip,
                ["steps"] = UserInput.Get(T2IParamTypes.Steps),
                ["prompt"] = prompt,
                ["width"] = enhance ? (int)Utilities.RoundToPrecision(width * mult, 64) : width,
                ["height"] = enhance ? (int)Utilities.RoundToPrecision(height * mult, 64) : height,
                ["target_width"] = width,
                ["target_height"] = height
            }, id);
        }
        else if (model is not null && model.ModelClass is not null && model.ModelClass.ID == "stable-diffusion-xl-v1-base")
        {
            node = CreateNode("CLIPTextEncodeSDXL", new JObject()
            {
                ["clip"] = clip,
                ["text_g"] = prompt,
                ["text_l"] = prompt,
                ["crop_w"] = 0,
                ["crop_h"] = 0,
                ["width"] = enhance ? (int)Utilities.RoundToPrecision(width * mult, 64) : width,
                ["height"] = enhance ? (int)Utilities.RoundToPrecision(height * mult, 64) : height,
                ["target_width"] = width,
                ["target_height"] = height
            }, id);
        }
        else
        {
            node = CreateNode("CLIPTextEncode", new JObject()
            {
                ["clip"] = clip,
                ["text"] = prompt
            }, id);
        }
        return [node, 0];
    }

    /// <summary>Creates a "CLIPTextEncode" or equivalent node for the given input, with support for '&lt;break&gt;' syntax.</summary>
    public JArray CreateConditioningLine(string prompt, JArray clip, T2IModel model, bool isPositive, string id = null)
    {
        string[] breaks = prompt.Split("<break>", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (breaks.Length <= 1)
        {
            return CreateConditioningDirect(prompt, clip, model, isPositive, id);
        }
        JArray first = CreateConditioningDirect(breaks[0], clip, model, isPositive);
        for (int i = 1; i < breaks.Length; i++)
        {
            JArray second = CreateConditioningDirect(breaks[i], clip, model, isPositive);
            string concatted = CreateNode("ConditioningConcat", new JObject()
            {
                ["conditioning_to"] = first,
                ["conditioning_from"] = second
            });
            first = [concatted, 0];
        }
        return first;
    }

    public record struct RegionHelper(JArray PartCond, JArray Mask);

    /// <summary>Creates a "CLIPTextEncode" or equivalent node for the given input, applying prompt-given conditioning modifiers as relevant.</summary>
    public JArray CreateConditioning(string prompt, JArray clip, T2IModel model, bool isPositive, string firstId = null)
    {
        PromptRegion regionalizer = new(prompt);
        JArray globalCond = CreateConditioningLine(regionalizer.GlobalPrompt, clip, model, isPositive, firstId);
        if (!isPositive && string.IsNullOrWhiteSpace(prompt) && UserInput.Get(T2IParamTypes.ZeroNegative, false))
        {
            string zeroed = CreateNode("ConditioningZeroOut", new JObject()
            {
                ["conditioning"] = globalCond
            });
            return [zeroed, 0];
        }
        PromptRegion.Part[] parts = regionalizer.Parts.Where(p => p.Type == PromptRegion.PartType.Object || p.Type == PromptRegion.PartType.Region).ToArray();
        if (parts.IsEmpty())
        {
            return globalCond;
        }
        string gligenModel = UserInput.Get(ComfyUIBackendExtension.GligenModel, "None");
        if (gligenModel != "None")
        {
            string gligenLoader = NodeHelpers.GetOrCreate("gligen_loader", () =>
            {
                return CreateNode("GLIGENLoader", new JObject()
                {
                    ["gligen_name"] = gligenModel
                });
            });
            int width = UserInput.Get(T2IParamTypes.Width, 1024);
            int height = UserInput.GetImageHeight();
            JArray lastCond = globalCond;
            foreach (PromptRegion.Part part in parts)
            {
                string applied = CreateNode("GLIGENTextBoxApply", new JObject()
                {
                    ["gligen_textbox_model"] = new JArray() { gligenLoader, 0 },
                    ["clip"] = clip,
                    ["conditioning_to"] = lastCond,
                    ["text"] = part.Prompt,
                    ["x"] = part.X * width,
                    ["y"] = part.Y * height,
                    ["width"] = part.Width * width,
                    ["height"] = part.Height * height
                });
                lastCond = [applied, 0];
            }
            return lastCond;
        }
        double globalStrength = UserInput.Get(T2IParamTypes.GlobalRegionFactor, 0.5);
        List<RegionHelper> regions = [];
        JArray lastMergedMask = null;
        foreach (PromptRegion.Part part in parts)
        {
            JArray partCond = CreateConditioningLine(part.Prompt, clip, model, isPositive);
            string regionNode = CreateNode("SwarmSquareMaskFromPercent", new JObject()
            {
                ["x"] = part.X,
                ["y"] = part.Y,
                ["width"] = part.Width,
                ["height"] = part.Height,
                ["strength"] = Math.Abs(part.Strength)
            });
            if (part.Strength < 0)
            {
                regionNode = CreateNode("InvertMask", new JObject()
                {
                    ["mask"] = new JArray() { regionNode, 0 }
                });
            }
            RegionHelper region = new(partCond, [regionNode, 0]);
            regions.Add(region);
            if (lastMergedMask is null)
            {
                lastMergedMask = region.Mask;
            }
            else
            {
                string overlapped = CreateNode("SwarmOverMergeMasksForOverlapFix", new JObject()
                {
                    ["mask_a"] = lastMergedMask,
                    ["mask_b"] = region.Mask
                });
                lastMergedMask = [overlapped, 0];
            }
        }
        string globalMask = CreateNode("SwarmSquareMaskFromPercent", new JObject()
        {
            ["x"] = 0,
            ["y"] = 0,
            ["width"] = 1,
            ["height"] = 1,
            ["strength"] = 1
        });
        string maskBackground = CreateNode("SwarmExcludeFromMask", new JObject()
        {
            ["main_mask"] = new JArray() { globalMask, 0 },
            ["exclude_mask"] = lastMergedMask
        });
        string backgroundPrompt = string.IsNullOrWhiteSpace(regionalizer.BackgroundPrompt) ? regionalizer.GlobalPrompt : regionalizer.BackgroundPrompt;
        JArray backgroundCond = CreateConditioningLine(backgroundPrompt, clip, model, isPositive);
        string mainConditioning = CreateNode("ConditioningSetMask", new JObject()
        {
            ["conditioning"] = backgroundCond,
            ["mask"] = new JArray() { maskBackground, 0 },
            ["strength"] = 1 - globalStrength,
            ["set_cond_area"] = "default"
        });
        EnableDifferential();
        DebugMask([maskBackground, 0]);
        void DebugMask(JArray mask)
        {
            if (UserInput.Get(ComfyUIBackendExtension.DebugRegionalPrompting))
            {
                string imgNode = CreateNode("MaskToImage", new JObject()
                {
                    ["mask"] = mask
                });
                CreateImageSaveNode([imgNode, 0]);
            }
        }
        foreach (RegionHelper region in regions)
        {
            string overlapped = CreateNode("SwarmCleanOverlapMasksExceptSelf", new JObject()
            {
                ["mask_self"] = region.Mask,
                ["mask_merged"] = lastMergedMask
            });
            DebugMask([overlapped, 0]);
            string regionCond = CreateNode("ConditioningSetMask", new JObject()
            {
                ["conditioning"] = region.PartCond,
                ["mask"] = new JArray() { overlapped, 0 },
                ["strength"] = 1 - globalStrength,
                ["set_cond_area"] = "default"
            });
            mainConditioning = CreateNode("ConditioningCombine", new JObject()
            {
                ["conditioning_1"] = new JArray() { mainConditioning, 0 },
                ["conditioning_2"] = new JArray() { regionCond, 0 }
            });
        }
        string globalCondApplied = CreateNode("ConditioningSetMask", new JObject()
        {
            ["conditioning"] = globalCond,
            ["mask"] = new JArray() { globalMask, 0 },
            ["strength"] = globalStrength,
            ["set_cond_area"] = "default"
        });
        string finalCond = CreateNode("ConditioningCombine", new JObject()
        {
            ["conditioning_1"] = new JArray() { mainConditioning, 0 },
            ["conditioning_2"] = new JArray() { globalCondApplied, 0 }
        });
        return new(finalCond, 0);
    }
}
