#if UNITY_5_3_OR_NEWER && NPCIFY_SIGHT_WEBCAM
using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// The webcam of sight. Unity adds the camera permission to an Android build, and a macOS or iOS
// build needs a Camera Usage Description, when a script uses WebCamTexture. So this file compiles
// only with the scripting define NPCIFY_SIGHT_WEBCAM: a game without it keeps its permissions.

namespace Npcify.Sdk.Sight
{
    public sealed partial class NpcifySightOptions
    {
        /// <summary>
        /// A webcam (source "camera"). A webcam that does not play yet: the SDK asks for the camera
        /// permission, starts it, and stops it at the end. A webcam that plays stays yours.
        /// </summary>
        public WebCamTexture WebCam { get; set; }
    }
}

namespace Npcify.Sdk.Unity
{
    using Npcify.Sdk.Sight;

    /// <summary>The pictures of a webcam for sight (source "camera").</summary>
    public sealed class NpcifyWebCamSight : INpcifySightSource
    {
        private const float StartSeconds = 5f;
        private const float PermissionSeconds = 30f;
        // A WebCamTexture is 16 x 16 until its first picture.
        private const int PlaceholderSize = 16;

        private readonly MonoBehaviour _host;
        private readonly WebCamTexture _webCam;
        private readonly NpcifySightCapture _capture;
        private bool _owned;
        private bool _stopped;

        public NpcifyWebCamSight(MonoBehaviour host, WebCamTexture webCam)
        {
            _host = host != null ? host : throw new ArgumentNullException(nameof(host));
            _webCam = webCam != null ? webCam : throw new ArgumentNullException(nameof(webCam));
            _capture = new NpcifySightCapture(
                host,
                "camera",
                () => _webCam != null && _webCam.width > PlaceholderSize ? _webCam : null,
                () => _webCam != null && _webCam.videoVerticallyMirrored,
                () => !_stopped && _webCam != null && _webCam.isPlaying);
        }

        public string Kind => "camera";

        public bool Live => _capture.Live;

        public void Open(Action<string> done)
        {
            _host.StartCoroutine(OpenWebCam(done));
        }

        public void ReadLuminance(Action<byte[]> done)
        {
            _capture.ReadLuminance(done);
        }

        public void Capture(Action<NpcifySightFrame> done)
        {
            _capture.Capture(done);
        }

        public void Stop()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            if (_owned && _webCam != null && _webCam.isPlaying)
            {
                _webCam.Stop();
            }
            _capture.Stop();
        }

        private IEnumerator OpenWebCam(Action<string> done)
        {
            if (!_webCam.isPlaying)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                {
                    Permission.RequestUserPermission(Permission.Camera);
                    var asked = Time.realtimeSinceStartup;
                    while (!_stopped && !Permission.HasUserAuthorizedPermission(Permission.Camera) &&
                           Time.realtimeSinceStartup - asked < PermissionSeconds)
                    {
                        yield return null;
                    }
                    if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                    {
                        done(NpcifySightCodes.CameraPermissionDenied);
                        yield break;
                    }
                }
#else
                if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
                }
                if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    done(NpcifySightCodes.CameraPermissionDenied);
                    yield break;
                }
#endif
                if (_stopped || WebCamTexture.devices.Length == 0)
                {
                    done(NpcifySightCodes.CameraUnavailable);
                    yield break;
                }
                _webCam.Play();
                _owned = true;
            }
            var started = Time.realtimeSinceStartup;
            while (!_stopped && _webCam.isPlaying && _webCam.width <= PlaceholderSize &&
                   Time.realtimeSinceStartup - started < StartSeconds)
            {
                yield return null;
            }
            if (_stopped || !_webCam.isPlaying || _webCam.width <= PlaceholderSize)
            {
                done(NpcifySightCodes.CameraUnavailable);
                yield break;
            }
            _capture.Open(done);
        }
    }
}
#endif
