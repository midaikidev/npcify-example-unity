#if UNITY_5_3_OR_NEWER
using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Npcify.Sdk.Sight
{
    /// <summary>
    /// The source of sight in Unity. Set one of them; none: the screen (the player's view). A
    /// webcam (WebCam) needs the scripting define NPCIFY_SIGHT_WEBCAM (NpcifyWebCamSight.cs).
    /// </summary>
    public sealed partial class NpcifySightOptions
    {
        /// <summary>
        /// A camera of the game (source "game"): its targetTexture when it has one, else the screen
        /// at the end of the frame, with the UI.
        /// </summary>
        public Camera Camera { get; set; }

        /// <summary>A render texture of the game (source "game"), for example the target of a camera at the character's eyes.</summary>
        public RenderTexture RenderTexture { get; set; }
    }
}

namespace Npcify.Sdk.Unity
{
    using Npcify.Sdk.Sight;

    /// <summary>
    /// The pictures of sight in Unity. The GPU scales the picture (Graphics.Blit) and
    /// AsyncGPUReadback reads it without a stall. The luma grid is mip 5 of a 1024 x 576 copy
    /// (32 x 18, each cell the mean of 32 x 32 pixels); a frame is a copy that fits inside
    /// 640 x 360, encoded to JPEG on a worker thread.
    /// </summary>
    public sealed class NpcifySightCapture : INpcifySightSource
    {
        /// <summary>The main-thread work of a motion check (the copies, the readback request and the luma).</summary>
        public static readonly ProfilerMarker GridMarker = new ProfilerMarker("Npcify.Sight.Grid");

        /// <summary>The main-thread work of a frame (the copy, the readback request and the copy of its pixels).</summary>
        public static readonly ProfilerMarker FrameMarker = new ProfilerMarker("Npcify.Sight.Frame");

        private const int GridCopyWidth = 1024;
        private const int GridCopyHeight = 576;
        private const int GridMip = 5;
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();

        private readonly MonoBehaviour _host;
        private readonly string _kind = "game";
        private readonly Camera _camera;
        private readonly RenderTexture _texture;
        // A texture of another source (a webcam): null while it has no picture.
        private readonly Func<Texture> _picture;
        private readonly Func<bool> _pictureFlipped;
        private readonly Func<bool> _pictureLive;
        private readonly bool _screen;
        private bool _stopped;
        private RenderTexture _gridCopy;
        private RenderTexture _frameCopy;
        private RenderTexture _screenCopy;
        private int _screenCopyFrame = -1;
        // The pixels of the frame that the worker encodes. One frame is in flight at a time, so
        // the next capture can use the buffer again.
        private byte[] _framePixels;

        /// <param name="host">Runs the coroutines of the screen copies.</param>
        public NpcifySightCapture(MonoBehaviour host, NpcifySightOptions options)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            options = options ?? new NpcifySightOptions();
            if (options.RenderTexture != null)
            {
                _texture = options.RenderTexture;
            }
            else if (options.Camera != null)
            {
                _camera = options.Camera;
            }
            else
            {
                _screen = true;
            }
        }

        /// <summary>
        /// The pictures of a texture of another source, for example a webcam (NpcifyWebCamSight).
        /// </summary>
        /// <param name="kind">"camera" or "game".</param>
        /// <param name="picture">The texture now, or null while it has no picture.</param>
        /// <param name="flipped">True when the picture is upside down.</param>
        /// <param name="live">False after the source ended.</param>
        public NpcifySightCapture(MonoBehaviour host, string kind, Func<Texture> picture, Func<bool> flipped, Func<bool> live)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            _kind = kind ?? "game";
            _picture = picture ?? throw new ArgumentNullException(nameof(picture));
            _pictureFlipped = flipped;
            _pictureLive = live;
        }

        /// <summary>The source of the options: a webcam (with NPCIFY_SIGHT_WEBCAM), a camera, a render texture or the screen.</summary>
        public static INpcifySightSource ForOptions(MonoBehaviour host, NpcifySightOptions options)
        {
#if NPCIFY_SIGHT_WEBCAM
            if (options?.WebCam != null)
            {
                return new NpcifyWebCamSight(host, options.WebCam);
            }
#endif
            return new NpcifySightCapture(host, options);
        }

        public string Kind => _kind;

        public bool Live
        {
            get
            {
                if (_stopped)
                {
                    return false;
                }
                if (_screen)
                {
                    return true;
                }
                if (_picture != null)
                {
                    return _pictureLive == null || _pictureLive();
                }
                // A destroyed Unity object compares equal to null.
                return _camera != null || _texture != null;
            }
        }

        public void Open(Action<string> done)
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                done(_kind == "camera" ? NpcifySightCodes.CameraUnavailable : NpcifySightCodes.SourceUnavailable);
                return;
            }
            done(Live ? null : _kind == "camera" ? NpcifySightCodes.CameraUnavailable : NpcifySightCodes.SourceUnavailable);
        }

        public void ReadLuminance(Action<byte[]> done)
        {
            WithPicture(
                (texture, flip, width, height) =>
                {
                    using var marker = GridMarker.Auto();
                    _gridCopy = Target(_gridCopy, GridCopyWidth, GridCopyHeight, true);
                    Copy(texture, _gridCopy, flip);
                    _gridCopy.GenerateMips();
                    AsyncGPUReadback.Request(_gridCopy, GridMip, TextureFormat.RGBA32, request =>
                    {
                        using var readMarker = GridMarker.Auto();
                        if (request.hasError || _stopped)
                        {
                            done(null);
                            return;
                        }
                        var data = request.GetData<byte>();
                        var luma = new byte[NpcifySightLimits.GridWidth * NpcifySightLimits.GridHeight];
                        if (data.Length < luma.Length * 4)
                        {
                            done(null);
                            return;
                        }
                        for (var index = 0; index < luma.Length; index++)
                        {
                            var offset = index * 4;
                            luma[index] = NpcifySightImage.Luma(data[offset], data[offset + 1], data[offset + 2]);
                        }
                        done(luma);
                    });
                },
                () => done(null));
        }

        public void Capture(Action<NpcifySightFrame> done)
        {
            WithPicture(
                (texture, flip, width, height) =>
                {
                    using var marker = FrameMarker.Auto();
                    NpcifySightImage.FitCaptureSize(width, height, out var frameWidth, out var frameHeight);
                    _frameCopy = Target(_frameCopy, frameWidth, frameHeight, false);
                    Copy(texture, _frameCopy, flip);
                    AsyncGPUReadback.Request(_frameCopy, 0, TextureFormat.RGBA32, request =>
                    {
                        using var readMarker = FrameMarker.Auto();
                        if (request.hasError || _stopped)
                        {
                            done(null);
                            return;
                        }
                        var data = request.GetData<byte>();
                        if (_framePixels == null || _framePixels.Length != data.Length)
                        {
                            _framePixels = new byte[data.Length];
                        }
                        var pixels = _framePixels;
                        data.CopyTo(pixels);
                        // JPEG encoding takes milliseconds: not on the main thread.
                        Task.Run(() => Encode(pixels, frameWidth, frameHeight)).ContinueWith(
                            encoded => done(encoded.Status == TaskStatus.RanToCompletion ? encoded.Result : null),
                            TaskScheduler.Default);
                    });
                },
                () => done(null));
        }

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            Release(ref _gridCopy);
            Release(ref _frameCopy);
            Release(ref _screenCopy);
        }

        /// <summary>The JPEG of RGBA pixels (bottom row first, as a texture): quality 70, then 50 once when it is above 200 KiB.</summary>
        public static NpcifySightFrame Encode(byte[] rgba, int width, int height)
        {
            var format = GraphicsFormat.R8G8B8A8_UNorm;
            var jpeg = ImageConversion.EncodeArrayToJPG(rgba, format, (uint)width, (uint)height, 0, NpcifySightLimits.JpegQuality);
            if (jpeg != null && jpeg.Length > NpcifySightLimits.MaxFrameBytes)
            {
                jpeg = ImageConversion.EncodeArrayToJPG(rgba, format, (uint)width, (uint)height, 0, NpcifySightLimits.SmallerJpegQuality);
            }
            return jpeg == null || jpeg.Length == 0 || jpeg.Length > NpcifySightLimits.MaxFrameBytes
                ? null
                : new NpcifySightFrame { Jpeg = jpeg, Width = width, Height = height };
        }

        // Runs use with the current picture: a camera target, a texture or a webcam now, the
        // screen at the end of the frame. Without a picture it runs none.
        private void WithPicture(Action<Texture, bool, int, int> use, Action none)
        {
            if (!Live || !_host.isActiveAndEnabled)
            {
                none();
                return;
            }
            if (_picture != null)
            {
                var picture = _picture();
                if (picture == null)
                {
                    none();
                    return;
                }
                use(picture, _pictureFlipped != null && _pictureFlipped(), picture.width, picture.height);
                return;
            }
            var texture = _texture != null ? _texture : _camera != null ? _camera.targetTexture : null;
            if (texture != null)
            {
                use(texture, false, texture.width, texture.height);
                return;
            }
            _host.StartCoroutine(AtEndOfFrame(use, none));
        }

        // The screen after all cameras and the UI rendered.
        private IEnumerator AtEndOfFrame(Action<Texture, bool, int, int> use, Action none)
        {
            yield return EndOfFrame;
            if (!Live || Screen.width <= 0 || Screen.height <= 0)
            {
                none();
                yield break;
            }
            if (_screenCopyFrame != Time.frameCount || _screenCopy == null ||
                _screenCopy.width != Screen.width || _screenCopy.height != Screen.height)
            {
                using var marker = GridMarker.Auto();
                _screenCopy = Target(_screenCopy, Screen.width, Screen.height, false);
                ScreenCapture.CaptureScreenshotIntoRenderTexture(_screenCopy);
                _screenCopyFrame = Time.frameCount;
            }
            // The screen copy is upside down where texture coordinates start at the top.
            use(_screenCopy, SystemInfo.graphicsUVStartsAtTop, _screenCopy.width, _screenCopy.height);
        }

        private static void Copy(Texture source, RenderTexture target, bool flip)
        {
            if (flip)
            {
                Graphics.Blit(source, target, new Vector2(1f, -1f), new Vector2(0f, 1f));
            }
            else
            {
                Graphics.Blit(source, target);
            }
        }

        // A render texture of the size; the old one is released when the size changed.
        private static RenderTexture Target(RenderTexture current, int width, int height, bool mips)
        {
            if (current != null && current.width == width && current.height == height)
            {
                return current;
            }
            Release(ref current);
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "npcify-sight",
                useMipMap = mips,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            target.Create();
            return target;
        }

        private static void Release(ref RenderTexture target)
        {
            if (target == null)
            {
                return;
            }
            target.Release();
            UnityEngine.Object.Destroy(target);
            target = null;
        }
    }
}
#endif
