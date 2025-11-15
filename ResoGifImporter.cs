using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using HarmonyLib;
using Renderite.Shared;
using ResoniteModLoader;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;       
using System.Text;

namespace ResoGifImporter
{
    public class ResoGifImporter : ResoniteMod
    {
        public override string Name => "ResoGifImporter";
        public override string Author => "zahndy";
        public override string Version => "1.0.0";
        public override string Link => "https://github.com/zahndy/ResoGifImporter";

        [AutoRegisterConfigKey]
        public static ModConfigurationKey<bool> KEY_SQUARE = new ModConfigurationKey<bool>(
            "Square spritesheet",
            "Generate square spritesheet",
            () => true);
        public static ModConfiguration? config;

        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("com.zahndy.gifimporter");
            harmony.PatchAll();
            config = GetConfiguration();
        }

        [HarmonyPatch(typeof(ImageImporter), "ImportImage")]
        class GifImporterPatch
        {
            public static bool Prefix(ref Task<Result> __result, ImportItem item, Slot targetSlot, bool addCollider,
                ImageProjection projection, StereoLayout stereoLayout, float3? forward, TextureConversion convert,
                bool setupScreenshotMetadata, bool pointFiltering, bool uncompressed, bool alphaBleed, bool stripMetadata)
            {
                Uri? uri = item.assetUri ?? (Uri.TryCreate(item.filePath, UriKind.Absolute, out var u) ? u : null);
                Image<Rgba32>? image = null;
                bool validGif = false;

                LocalDB localDB = targetSlot.World.Engine.LocalDB;

                // Local file import vs URL import
                if (uri == null)
                {
                    validGif = false;
                }
                else if (uri.Scheme == "file" && item.filePath != null)
                {
                    // Check file header
                    using (FileStream fs = new FileStream(item.filePath, FileMode.Open, FileAccess.Read))
                    {
                        byte[] headerBytes = new byte[6]; // GIF header is 6 bytes
                        int bytesRead = fs.Read(headerBytes, 0, headerBytes.Length);

                        if (bytesRead != headerBytes.Length)
                        {
                            Debug("File is too short to be a gif");
                            return true;
                        }

                        string header = Encoding.ASCII.GetString(headerBytes);

                        if (header != "GIF87a" && header != "GIF89a")
                        {
                            Debug("Magic number doesn't match GIF magic number");
                            return true;
                        }

                        validGif = true;
                    }
                    image = Image.Load<Rgba32>(item.filePath);
                }
                else if (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "resdb")
                {
                    using (var httpClient = new System.Net.Http.HttpClient())
                    {
                        using (var stream = httpClient.GetStreamAsync(uri).GetAwaiter().GetResult())
                        {
                            image = Image.Load<Rgba32>(stream);
                        }
                        var response = httpClient.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, uri)).GetAwaiter().GetResult();
                        var type = response.Content.Headers.ContentType?.MediaType;
                        validGif = type == "image/gif";
                    }
                }
                else if (uri.Scheme == "resdb")
                {
                    validGif = true;
                }

                if (!validGif)
                {
                    Debug($"{item.itemName} is not a gif, returning true");
                    image?.Dispose();
                    return true;
                }

                __result = targetSlot.StartTask<Result>(async delegate ()
                {
                    await default(ToBackground);

                    // Load the image
                    if (uri?.Scheme == "resdb")
                    {
                        Debug($"Awaiting asset from resdb uri...");
                        image = Image.Load<Rgba32>(await localDB.TryOpenAsset(uri));
                    }

                    int frameCount = 0;
                    float frameDelay = 0;
                    var frameWidth = 0;
                    var frameHeight = 0;
                    int gifRows = 0;
                    int gifCols = 0;
                    Image<Rgba32>? spriteSheet = null;
                    string spritePath = Path.GetTempFileName();

                    try
                    {
                        frameCount = image!.Frames.Count;
                        frameWidth = image.Width;
                        frameHeight = image.Height;

                        // Get the times stored in the image
                        int[] frameDelays = new int[frameCount];
                        var gifMetadata = image.Metadata.GetGifMetadata();
                        
                        for (int i = 0; i < frameCount; i++)
                        {
                            var frameMetadata = image.Frames[i].Metadata.GetGifMetadata();
                            frameDelays[i] = frameMetadata?.FrameDelay ?? 10;
                            if (frameDelays[i] == 0)
                                frameDelays[i] = 10;
                        }

                        if (config!.GetValue(KEY_SQUARE))
                        {
                            // Calculate amount of cols and rows
                            float ratio = (float)frameWidth / frameHeight;
                            var cols = MathX.Sqrt(frameCount / ratio);
                            gifCols = MathX.RoundToInt(cols);
                            gifRows = frameCount / gifCols + ((frameCount % gifCols != 0) ? 1 : 0);
                        }
                        else
                        {
                            gifCols = frameCount;
                            gifRows = 1;
                        }

                        spriteSheet = new Image<Rgba32>(frameWidth * gifCols, frameHeight * gifRows);

                        for (int i = 0; i < gifRows; i++)
                        {
                            for (int j = 0; j < gifCols; j++)
                            {
                                if (i * gifCols + j >= frameCount)
                                    break;

                                int frameIndex = i * gifCols + j;
                                var frame = image.Frames[frameIndex];
                                int offsetX = frameWidth * j;
                                int offsetY = frameHeight * i;

                                for (int y = 0; y < frameHeight; y++)
                                {
                                    for (int x = 0; x < frameWidth; x++)
                                    {
                                        spriteSheet[offsetX + x, offsetY + y] = frame[x, y];
                                    }
                                }

                                frameDelay += frameDelays[frameIndex];
                            }
                        }

                        frameDelay = 100 * frameCount / frameDelay;

                        // Save the image
                        string extension = ".png";
                        switch (convert)
                        {
                            case TextureConversion.PNG:
                                extension = ".png";
                                break;
                            case TextureConversion.JPEG:
                                extension = ".jpg";
                                break;
                        }
                        spritePath = Path.ChangeExtension(spritePath, extension);

                        await spriteSheet.SaveAsync(spritePath);
                    }
                    catch (Exception e)
                    {
                        Error("Failed to read GIF");
                        return Result.Failure(e);
                    }
                    finally
                    {
                        image?.Dispose();
                        spriteSheet?.Dispose();
                    }

                    Debug($"Image saved as {spritePath}");

                    Uri localUri = await localDB.ImportLocalAssetAsync(spritePath,
                        LocalDB.ImportLocation.Copy).ConfigureAwait(continueOnCapturedContext: false);

                    File.Delete(spritePath);

                    await default(ToWorld);

                    targetSlot.Name = item.itemName;
                    if (forward.HasValue)
                    {
                        float3 from = forward.Value;
                        float3 to = float3.Forward;
                        targetSlot.LocalRotation = floatQ.FromToRotation(in from, in to);
                    }

                    StaticTexture2D tex = targetSlot.AttachComponent<StaticTexture2D>();
                    tex.URL.Value = localUri;
                    if (pointFiltering)
                    {
                        tex.FilterMode.Value = TextureFilterMode.Point;
                    }
                    if (uncompressed)
                    {
                        tex.Uncompressed.Value = true;
                        tex.PowerOfTwoAlignThreshold.Value = 0f;
                    }

                    ImageImporter.SetupTextureProxyComponents(targetSlot, tex, stereoLayout, projection, setupScreenshotMetadata);
                    if (projection != 0)
                        ImageImporter.Create360Sphere(targetSlot, tex, stereoLayout, projection, addCollider);
                    else
                    {
                        while (!tex.IsAssetAvailable) await default(NextUpdate);
                        ImageImporter.CreateQuad(targetSlot, tex, stereoLayout, addCollider);
                    }

                    if (setupScreenshotMetadata)
                        targetSlot.GetComponentInChildren<PhotoMetadata>()?.NotifyOfScreenshot();

                    // Set up GIF parameters
                    AtlasInfo _AtlasInfo = targetSlot.AttachComponent<AtlasInfo>();
                    UVAtlasAnimator _UVAtlasAnimator = targetSlot.AttachComponent<UVAtlasAnimator>();
                    TimeIntDriver _TimeIntDriver = targetSlot.AttachComponent<TimeIntDriver>();
                    _AtlasInfo.GridFrames.Value = frameCount;
                    _AtlasInfo.GridSize.Value = new int2(gifCols, gifRows);
                    _TimeIntDriver.Scale.Value = frameDelay;
                    _TimeIntDriver.Repeat.Value = _AtlasInfo.GridFrames.Value;
                    _TimeIntDriver.Target.Target = _UVAtlasAnimator.Frame;
                    _UVAtlasAnimator.AtlasInfo.Target = _AtlasInfo;

                    TextureSizeDriver _TextureSizeDriver = targetSlot.GetComponent<TextureSizeDriver>();
                    _TextureSizeDriver.Premultiply.Value = new float2(gifRows, gifCols);

                    UnlitMaterial _UnlitMaterial = targetSlot.GetComponent<UnlitMaterial>();
                    _UVAtlasAnimator.ScaleField.Target = _UnlitMaterial.TextureScale;
                    _UVAtlasAnimator.OffsetField.Target = _UnlitMaterial.TextureOffset;
                    _UnlitMaterial.BlendMode.Value = BlendMode.Cutout;

                    // Set inventory preview to first frame
                    ItemTextureThumbnailSource _inventoryPreview = targetSlot.GetComponent<ItemTextureThumbnailSource>();
                    _inventoryPreview.Crop.Value = new Rect(0, 0, 1f / (float)gifCols, 1f / (float)gifRows);

                    return Result.Success();
                });

                return false;
            }
        }
    }
}
