﻿using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Accounts;
using StableSwarmUI.Backends;
using StableSwarmUI.Core;
using StableSwarmUI.Utils;
using StableSwarmUI.WebAPI;
using System.Diagnostics;
using System.IO;

namespace StableSwarmUI.Text2Image
{
    /// <summary>Central core handler for text-to-image processing.</summary>
    public static class T2IEngine
    {
        /// <summary>Extension event, fired before images will be generated, just after the request is received.
        /// No backend is claimed yet.
        /// Use <see cref="InvalidOperationException"/> for a user-readable refusal message.</summary>
        public static Action<PreGenerationEventParams> PreGenerateEvent;

        public record class PreGenerationEventParams(T2IParamInput UserInput);

        /// <summary>Extension event, fired after images were generated, but before saving the result.
        /// Backend is already released, but the gen request is not marked completed.
        /// Ran before metadata is applied.
        /// Use "RefuseImage" to mark an image as refused. Note that generation previews may have already been shown to a user, if that feature is enabled on the server.
        /// Use <see cref="InvalidDataException"/> for a user-readable hard-refusal message.</summary>
        public static Action<PostGenerationEventParams> PostGenerateEvent;

        /// <summary>Paramters for <see cref="PostGenerateEvent"/>.</summary>
        public record class PostGenerationEventParams(Image Image, T2IParamInput UserInput, Action RefuseImage);

        /// <summary>Extension event, fired after a batch of images were generated.
        /// Use "RefuseImage" to mark an image as removed. Note that it may have already been shown to a user, when the live result websocket API is in use.</summary>
        public static Action<PostBatchEventParams> PostBatchEvent;

        /// <summary>Parameters for <see cref="PostBatchEvent"/>.</summary>
        public record class PostBatchEventParams(T2IParamInput UserInput, ImageInBatch[] Images);

        /// <summary>Represents a single image within a batch of images, for <see cref="PostBatchEvent"/>.</summary>
        public record class ImageInBatch(Image Image, Action RefuseImage);

        /// <summary>Helper to create a function to match a backend to a user input request.</summary>
        public static Func<BackendHandler.T2IBackendData, bool> BackendMatcherFor(T2IParamInput user_input)
        {
            string type = user_input.Get(T2IParamTypes.BackendType, "any");
            bool requireId = user_input.TryGet(T2IParamTypes.ExactBackendID, out int reqId);
            string typeLow = type.ToLowerFast();
            return backend =>
            {
                if (typeLow != "any" && typeLow != backend.Backend.HandlerTypeData.ID.ToLowerFast())
                {
                    Logs.Verbose($"Filter out backend {backend.ID} as the Type is specified as {typeLow}, but the backend type is {backend.Backend.HandlerTypeData.ID.ToLowerFast()}");
                    return false;
                }
                if (requireId && backend.ID != reqId)
                {
                    Logs.Verbose($"Filter out backend {backend.ID} as the request requires backend ID {reqId}, but the backend ID is {backend.ID}");
                    return false;
                }
                HashSet<string> features = backend.Backend.SupportedFeatures.ToHashSet();
                foreach (string flag in user_input.RequiredFlags)
                {
                    if (!features.Contains(flag))
                    {
                        Logs.Verbose($"Filter out backend {backend.ID} as the request requires flag {flag}, but the backend does not support it");
                        return false;
                    }
                }
                if (backend.Backend.Models is not null)
                {
                    bool requireModel(T2IRegisteredParam<T2IModel> param, string type)
                    {
                        if (user_input.TryGet(param, out T2IModel model) && backend.Backend.Models.TryGetValue(type, out List<string> models) && !models.Contains(model.Name))
                        {
                            Logs.Verbose($"Filter out backend {backend.ID} as the request requires {type} model {model.Name}, but the backend does not have that model");
                            return false;
                        }
                        return true;
                    }
                    if (!requireModel(T2IParamTypes.Model, "Stable-Diffusion") || !requireModel(T2IParamTypes.RefinerModel, "Stable-Diffusion")
                        || !requireModel(T2IParamTypes.VAE, "VAE") || !requireModel(T2IParamTypes.ControlNetModel, "ControlNet"))
                    {
                        return false;
                    }
                    if (user_input.TryGet(T2IParamTypes.Loras, out List<string> loras) && backend.Backend.Models.TryGetValue("LoRA", out List<string> loraModels))
                    {
                        foreach (string lora in loras)
                        {
                            if (!loraModels.Contains(lora))
                            {
                                Logs.Verbose($"Filter out backend {backend.ID} as the request requires lora {lora}, but the backend does not have that lora");
                                return false;
                            }
                        }
                    }
                    if (user_input.ExtraMeta.TryGetValue("used_embeddings", out object usedEmbeds) && backend.Backend.Models.TryGetValue("Embedding", out List<string> embedModels))
                    {
                        foreach (string embed in (List<string>)usedEmbeds)
                        {
                            if (!embedModels.Contains(embed))
                            {
                                Logs.Verbose($"Filter out backend {backend.ID} as the request requires embedding {embed}, but the backend does not have that embedding");
                                return false;
                            }
                        }
                    }
                }
                return true;
            };
        }

        /// <summary>Internal handler route to create an image based on a user request.</summary>
        public static async Task CreateImageTask(T2IParamInput user_input, string batchId, Session.GenClaim claim, Action<JObject> output, Action<string> setError, bool isWS, float backendTimeoutMin, Action<Image, string> saveImages)
        {
            await CreateImageTask(user_input, batchId, claim, output, setError, isWS, backendTimeoutMin, saveImages, true);
        }

        /// <summary>Internal handler route to create an image based on a user request.</summary>
        public static async Task CreateImageTask(T2IParamInput user_input, string batchId, Session.GenClaim claim, Action<JObject> output, Action<string> setError, bool isWS, float backendTimeoutMin, Action<Image, string> saveImages, bool canCallTools)
        {
            Stopwatch timer = Stopwatch.StartNew();
            void sendStatus()
            {
                if (isWS && user_input.SourceSession is not null)
                {
                    output(BasicAPIFeatures.GetCurrentStatusRaw(user_input.SourceSession));
                }
            }
            if (claim.ShouldCancel)
            {
                return;
            }
            if (canCallTools)
            {
                string prompt = user_input.Get(T2IParamTypes.Prompt);
                if (prompt is not null && prompt.Contains("<object:"))
                {
                    Image multiImg = await T2IMultiStepObjectBuilder.CreateFullImage(prompt, user_input, batchId, claim, output, setError, isWS, backendTimeoutMin);
                    if (multiImg is not null)
                    {
                        user_input = user_input.Clone();
                        user_input.Set(T2IParamTypes.InitImage, multiImg);
                        user_input.Set(T2IParamTypes.InitImageCreativity, 0.7); // TODO: Configurable
                        user_input.Remove(T2IParamTypes.ControlNetModel);
                    }
                }
            }
            T2IBackendAccess backend;
            try
            {
                user_input.PreparsePromptLikes();
                PreGenerateEvent?.Invoke(new(user_input));
                claim.Extend(backendWaits: 1);
                sendStatus();
                backend = await Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(backendTimeoutMin), user_input.Get(T2IParamTypes.Model),
                    filter: BackendMatcherFor(user_input), session: user_input.SourceSession, notifyWillLoad: sendStatus, cancel: claim.InterruptToken);
            }
            catch (InvalidDataException ex)
            {
                setError($"Invalid data: {ex.Message}");
                return;
            }
            catch (InvalidOperationException ex)
            {
                setError($"Invalid operation: {ex.Message}");
                return;
            }
            catch (TimeoutException)
            {
                setError("Timeout! All backends are occupied with other tasks.");
                return;
            }
            finally
            {
                claim.Complete(backendWaits: 1);
                sendStatus();
            }
            if (claim.ShouldCancel)
            {
                backend?.Dispose();
                return;
            }
            try
            {
                claim.Extend(liveGens: 1);
                sendStatus();
                long prepTime;
                int numImagesGenned = 0;
                long lastGenTime = 0;
                string genTimeReport = "? failed!";
                void handleImage(Image img)
                {
                    if (img is not null)
                    {
                        lastGenTime = timer.ElapsedMilliseconds;
                        genTimeReport = $"{prepTime / 1000.0:0.00} (prep) and {(lastGenTime - prepTime) / 1000.0:0.00} (gen) seconds";
                        user_input.ExtraMeta["generation_time"] = genTimeReport;
                        bool refuse = false;
                        PostGenerateEvent?.Invoke(new(img, user_input, () => refuse = true));
                        if (refuse)
                        {
                            Logs.Info($"Refused an image.");
                        }
                        else
                        {
                            (img, string metadata) = user_input.SourceSession.ApplyMetadata(img, user_input, numImagesGenned);
                            saveImages(img, metadata);
                            numImagesGenned++;
                        }
                    }
                }
                using (backend)
                {
                    if (claim.ShouldCancel)
                    {
                        return;
                    }
                    prepTime = timer.ElapsedMilliseconds;
                    await backend.Backend.GenerateLive(user_input, batchId, obj =>
                    {
                        if (obj is Image img)
                        {
                            handleImage(img);
                        }
                        else
                        {
                            output(new JObject() { ["gen_progress"] = (JToken)obj });
                        }
                    });
                    if (numImagesGenned == 0)
                    {
                        Logs.Info("No images were generated (all refused, or failed).");
                        setError("No images were generated (all refused, or failed - check server logs for details).");
                    }
                    else if (numImagesGenned == 1)
                    {
                        Logs.Info($"Generated an image in {genTimeReport}");
                    }
                    else
                    {
                        Logs.Info($"Generated {numImagesGenned} images in {genTimeReport} ({((lastGenTime - prepTime) / numImagesGenned) / 1000.0:0.00} seconds per image)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is AggregateException ae)
                {
                    while (ae.InnerException is AggregateException e2 && e2 != ex && e2 != ae)
                    {
                        ae = e2;
                        ex = e2;
                    }
                }
                if (ex is AbstractT2IBackend.PleaseRedirectException)
                {
                    claim.Extend(gens: 1);
                    await CreateImageTask(user_input, batchId, claim, output, setError, isWS, backendTimeoutMin, saveImages, false);
                }
                else if (ex.InnerException is InvalidOperationException ioe)
                {
                    setError($"Invalid operation: {ioe.Message}");
                    return;
                }
                else if (ex.InnerException is InvalidDataException ide)
                {
                    setError($"Invalid data: {ide.Message}");
                    return;
                }
                else if (ex is TaskCanceledException)
                {
                    return;
                }
                else
                {
                    Logs.Error($"Internal error processing T2I request: {ex}");
                    setError("Something went wrong while generating images.");
                    return;
                }
            }
            finally
            {
                claim.Complete(gens: 1, liveGens: 1);
                sendStatus();
            }
        }
    }
}
