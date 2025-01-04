﻿using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using System.IO;
using System.Security.Cryptography;

namespace SwarmUI.Text2Image;

/// <summary>Represents the basic data around an (unloaded) Text2Image model.</summary>
public class T2IModel(T2IModelHandler handler, string folderPath, string filePath, string name)
{
    /// <summary>The backing handler that this model came from.</summary>
    public T2IModelHandler Handler = handler;

    /// <summary>Path to the main folder this is inside of.</summary>
    public string OriginatingFolderPath = folderPath;

    /// <summary>Name/ID of the model.</summary>
    public string Name = name;

    /// <summary>Full raw system filepath to this model.</summary>
    public string RawFilePath = filePath;

    /// <summary>If multiple copies of this model exist, these are other paths to that model.</summary>
    public List<string> OtherPaths = [];

    /// <summary>Proper title of the model, if identified.</summary>
    public string Title;

    /// <summary>Description text, if any, of the model.</summary>
    public string Description;

    /// <summary>URL or data blob of a preview image for this model.</summary>
    public string PreviewImage;

    /// <summary>This model's standard resolution, eg 1024x1024. 0 means unknown.</summary>
    public int StandardWidth, StandardHeight;

    /// <summary>If true, at least one backend has this model currently loaded.</summary>
    public bool AnyBackendsHaveLoaded = false;

    /// <summary>What class this model is, if known.</summary>
    public T2IModelClass ModelClass;

    /// <summary>Metadata about this model.</summary>
    public T2IModelHandler.ModelMetadataStore Metadata;

    /// <summary>Set of all model file extensions that are considered natively supported.</summary>
    public static HashSet<string> NativelySupportedModelExtensions = ["safetensors", "sft", "engine", "gguf"];

    /// <summary>Set of all model file extensions that are considered supported for legacy reasons only.</summary>
    public static HashSet<string> LegacyModelExtensions = ["ckpt", "pt", "pth", "bin"];

    /// <summary>Returns true if this is a 'diffusion_models' model instead of a regular checkpoint model.</summary>
    public bool IsDiffusionModelsFormat
    {
        get
        {
            string cleaned = OriginatingFolderPath.Replace('\\', '/').TrimEnd('/').ToLowerFast();
            return cleaned.EndsWithFast("/unet") || cleaned.EndsWithFast("/diffusion_models"); // Hacky but it works for now
        }
    }

    /// <summary>Returns the model's SHA-256 Tensor Hash - either from metadata, or by generating it from the file.
    /// Returns null if hashing is impossible (no handler, no metadata construct, no source file).
    /// Can optionally update cache and/or file when generating.</summary>
    /// <param name="updateCache">If true, the model data cache will be updated if the hash is calculated. Only set false if you're going to update the cache after.</param>
    /// <param name="resave">If true, the model file data will be updated if the hash is calculated. Only set false if you're going to update the file after.</param>
    /// <returns></returns>
    public string GetOrGenerateTensorHashSha256(bool updateCache = true, bool resave = true)
    {
        if (Metadata?.Hash is not null)
        {
            return Metadata.Hash;
        }
        if (Metadata is null || Handler is null || RawFilePath is null)
        {
            return null;
        }
        lock (Handler.ModificationLock)
        {
            if (Metadata.Hash is not null)
            {
                return Metadata.Hash;
            }
            using FileStream reader = File.OpenRead(RawFilePath);
            byte[] headerLen = new byte[8];
            reader.ReadExactly(headerLen, 0, 8);
            long len = BitConverter.ToInt64(headerLen, 0);
            if (len < 0 || len > 100 * 1024 * 1024)
            {
                return null;
            }
            reader.Seek(8 + len, SeekOrigin.Begin);
            string hash = "0x" + Utilities.BytesToHex(SHA256.HashData(reader));
            Metadata.Hash = hash;
            if (updateCache)
            {
                Handler.ResetMetadataFrom(this);
            }
            if (resave)
            {
                ResaveModel(reader);
            }
            return hash;
        }
    }

    /// <summary>Resaves the model's metadata to file (ie safetensors header, or companion json file).</summary>
    public void ResaveModel(FileStream reader = null)
    {
        lock (Handler.ModificationLock)
        {
            if (Metadata is null)
            {
                return;
            }
            string rawFilePrefix = RawFilePath.BeforeLast('.');
            string rawSwarmJsPath = $"{rawFilePrefix}.swarm.json";
            bool earlyEnd = (!RawFilePath.EndsWith(".safetensors") && !RawFilePath.EndsWith(".sft")) || Program.ServerSettings.Metadata.EditMetadataWriteJSON;
            void DoClear(string path)
            {
                string filePrefix = path.BeforeLast('.');
                string swarmjspath = $"{filePrefix}.swarm.json";
                if (File.Exists(swarmjspath))
                {
                    File.Delete(swarmjspath);
                }
                if (Program.ServerSettings.Paths.ClearStrayModelData)
                {
                    foreach (string altPath in T2IModelHandler.AllModelAttachedExtensions)
                    {
                        if (File.Exists($"{filePrefix}{altPath}"))
                        {
                            File.Delete($"{filePrefix}{altPath}");
                        }
                    }
                }
                if (earlyEnd)
                {
                    File.WriteAllText(swarmjspath, ToNetObject("modelspec.").ToString());
                }
            }
            DoClear(RawFilePath);
            if (Program.ServerSettings.Paths.EditMetadataAcrossAllDups)
            {
                foreach (string altPath in OtherPaths)
                {
                    DoClear(altPath);
                }
            }
            if (earlyEnd)
            {
                Logs.Debug($"Intentionally not reapplying metadata for model '{RawFilePath}', stored as json instead");
                return;
            }
            Logs.Debug($"Will reapply metadata for model '{RawFilePath}'");
            bool wasNull = reader is null;
            reader ??= File.OpenRead(RawFilePath);
            try
            {
                reader.Seek(0, SeekOrigin.Begin);
                byte[] headerLen = new byte[8];
                reader.ReadExactly(headerLen, 0, 8);
                long len = BitConverter.ToInt64(headerLen, 0);
                if (len < 0 || len > 100 * 1024 * 1024)
                {
                    Logs.Warning($"Model {Name} has invalid metadata length {len}, failing to store metadata, will place json copy in main folder.");
                    File.WriteAllText(rawSwarmJsPath, ToNetObject("modelspec.").ToString());
                    return;
                }
                byte[] header = new byte[len];
                reader.ReadExactly(header, 0, (int)len);
                string headerStr = Encoding.UTF8.GetString(header);
                JObject json = JObject.Parse(headerStr);
                JObject metaHeader = (json["__metadata__"] as JObject) ?? [];
                if (Metadata.Hash is null)
                {
                    // Metadata fix for when we generated hashes into the file metadata headers, but did not save them into the metadata cache
                    Metadata.Hash = (metaHeader?.ContainsKey("modelspec.hash_sha256") ?? false) ? metaHeader.Value<string>("modelspec.hash_sha256") : "0x" + Utilities.BytesToHex(SHA256.HashData(reader));
                    Handler.ResetMetadataFrom(this);
                }
                void specSet(string key, string val)
                {
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        metaHeader[$"modelspec.{key}"] = val;
                    }
                    else
                    {
                        metaHeader.Remove($"modelspec.{key}");
                    }
                }
                specSet("sai_model_spec", "1.0.0");
                specSet("title", Metadata.Title);
                specSet("architecture", Metadata.ModelClassType);
                specSet("author", Metadata.Author);
                specSet("description", Metadata.Description);
                specSet("thumbnail", Metadata.PreviewImage);
                specSet("license", Metadata.License);
                specSet("usage_hint", Metadata.UsageHint);
                specSet("trigger_phrase", Metadata.TriggerPhrase);
                specSet("tags", string.Join(",", Metadata.Tags ?? []));
                specSet("merged_from", Metadata.MergedFrom);
                specSet("date", Metadata.Date);
                specSet("preprocessor", Metadata.Preprocessor);
                specSet("resolution", $"{Metadata.StandardWidth}x{Metadata.StandardHeight}");
                specSet("prediction_type", Metadata.PredictionType);
                specSet("hash_sha256", Metadata.Hash);
                if (Metadata.IsNegativeEmbedding)
                {
                    specSet("is_negative_embedding", "true");
                }
                json["__metadata__"] = metaHeader;
                void HandleResave(string path)
                {
                    if (reader is null)
                    {
                        reader = File.OpenRead(path);
                        reader.Seek(0, SeekOrigin.Begin);
                        byte[] headerLen = new byte[8];
                        reader.ReadExactly(headerLen, 0, 8);
                        len = BitConverter.ToInt64(headerLen, 0);
                    }
                    {
                        using FileStream writer = File.OpenWrite(path + ".tmp");
                        byte[] headerBytes = Encoding.UTF8.GetBytes(json.ToString(Newtonsoft.Json.Formatting.None));
                        writer.Write(BitConverter.GetBytes(headerBytes.LongLength));
                        writer.Write(headerBytes);
                        reader.Seek(8 + len, SeekOrigin.Begin);
                        reader.CopyTo(writer);
                        reader.Dispose();
                        reader = null;
                    }
                    // Journalling replace to prevent data loss in event of a crash.
                    DateTime createTime = File.GetCreationTimeUtc(path);
                    File.Move(path, path + ".tmp2");
                    File.Move(path + ".tmp", path);
                    File.Delete(path + ".tmp2");
                    File.SetCreationTimeUtc(path, createTime);
                    Logs.Debug($"Completed metadata update for {path}");
                }
                HandleResave(RawFilePath);
                if (Program.ServerSettings.Paths.EditMetadataAcrossAllDups)
                {
                    foreach (string altPath in OtherPaths)
                    {
                        HandleResave(altPath);
                    }
                }
            }
            finally
            {
                if (wasNull)
                {
                    reader?.Dispose();
                }
            }
        }
    }

    /// <summary>Gets a networkable copy of this model's data.</summary>
    public JObject ToNetObject(string prefix = "")
    {
        return new JObject()
        {
            [$"{prefix}name"] = Name,
            [$"{prefix}title"] = Metadata?.Title,
            [$"{prefix}author"] = Metadata?.Author,
            [$"{prefix}description"] = Description,
            [$"{prefix}preview_image"] = PreviewImage,
            [$"{prefix}loaded"] = AnyBackendsHaveLoaded,
            [$"{prefix}architecture"] = ModelClass?.ID,
            [$"{prefix}class"] = ModelClass?.Name,
            [$"{prefix}compat_class"] = ModelClass?.CompatClass,
            [$"{prefix}resolution"] = $"{StandardWidth}x{StandardHeight}",
            [$"{prefix}standard_width"] = StandardWidth <= 0 ? ModelClass?.StandardWidth ?? 0 : StandardWidth,
            [$"{prefix}standard_height"] = StandardHeight <= 0 ? ModelClass?.StandardHeight ?? 0 : StandardHeight,
            [$"{prefix}license"] = Metadata?.License,
            [$"{prefix}date"] = Metadata?.Date,
            [$"{prefix}prediction_type"] = Metadata?.PredictionType,
            [$"{prefix}usage_hint"] = Metadata?.UsageHint,
            [$"{prefix}trigger_phrase"] = Metadata?.TriggerPhrase,
            [$"{prefix}merged_from"] = Metadata?.MergedFrom,
            [$"{prefix}tags"] = Metadata?.Tags is null ? null : new JArray(Metadata.Tags),
            [$"{prefix}is_supported_model_format"] = NativelySupportedModelExtensions.Contains(RawFilePath.AfterLast('.')),
            [$"{prefix}is_negative_embedding"] = Metadata?.IsNegativeEmbedding ?? false,
            [$"{prefix}local"] = true,
            [$"{prefix}time_created"] = Metadata?.TimeCreated ?? 0,
            [$"{prefix}time_modified"] = Metadata?.TimeModified ?? 0,
            [$"{prefix}hash"] = Metadata?.Hash ?? "",
            [$"{prefix}hash_sha256"] = Metadata?.Hash ?? "",
            [$"{prefix}special_format"] = Metadata?.SpecialFormat ?? ""
        };
    }

    public static T2IModel FromNetObject(JObject data)
    {
        return new T2IModel(null, null, null, $"{data["name"]}")
        {
            Title = $"{data["title"]}",
            Description = $"{data["description"]}",
            PreviewImage = $"{data["preview_image"]}",
            AnyBackendsHaveLoaded = (bool)data["loaded"],
            ModelClass = T2IModelClassSorter.ModelClasses.GetValueOrDefault($"{data["architecture"]}") ?? null,
            StandardWidth = (int)data["standard_width"],
            StandardHeight = (int)data["standard_height"]
        };
    }

    /// <summary>Get the safetensors header from a model.</summary>
    public static string GetSafetensorsHeaderFrom(string modelPath)
    {
        using FileStream file = File.OpenRead(modelPath);
        byte[] lenBuf = new byte[8];
        file.ReadExactly(lenBuf, 0, 8);
        long len = BitConverter.ToInt64(lenBuf, 0);
        if (len < 0 || len > 100 * 1024 * 1024)
        {
            throw new SwarmReadableErrorException($"Improper safetensors file {modelPath}. Wrong file type, or unreasonable header length: {len}");
        }
        byte[] dataBuf = new byte[len];
        file.ReadExactly(dataBuf, 0, (int)len);
        return Encoding.UTF8.GetString(dataBuf);
    }

    /// <summary>Returns the name of the model.</summary>
    public string ToString(string folderFormat)
    {
        return Name.Replace("/", folderFormat ?? $"{Path.DirectorySeparatorChar}");
    }

    /// <summary>Returns the name of the model.</summary>
    public override string ToString()
    {
        return Name.Replace('/', Path.DirectorySeparatorChar);
    }

    public static AsciiMatcher DangerousModelNameChars = new("\n\t,%*\"<>");

    /// <summary>Display any necessary warnings related to this model in logs.</summary>
    public void AutoWarn()
    {
        if (DangerousModelNameChars.ContainsAnyMatch(Name))
        {
            Logs.Warning($"{Handler?.ModelType} model '{Name}' contains special characters in its name, which might cause parsing issues. Consider renaming the file (or folder).");
        }
        if (Handler?.ModelType == "Embedding" && Name.Contains(' '))
        {
            Logs.Warning($"Embedding model '{Name}' contains spaces in its name, which will cause it to not parse properly on the backend. Please rename the file or folder to remove spaces.");
        }
    }
}
