using System;
using System.Collections.Generic;
using System.Globalization;
using Npcify.Sdk.Protocol;

namespace Npcify.Sdk.Sight
{
    /// <summary>
    /// Unavailable: the character info did not turn sight on, or the conversation is baked.
    /// Off: sight can start. Starting: the source opens and the conversation joins. On: frames go
    /// to the server.
    /// </summary>
    public enum NpcifySightState
    {
        Unavailable,
        Off,
        Starting,
        On,
    }

    /// <summary>The limits and defaults of sight (docs/sight.md, SIGHT_LIMITS and SIGHT_CAPTURE_SIZE of @npcify/shared).</summary>
    public static class NpcifySightLimits
    {
        /// <summary>The largest JPEG frame that the server accepts (200 KiB).</summary>
        public const int MaxFrameBytes = 200 * 1024;
        /// <summary>Frames fit inside 640 x 360 pixels. A smaller picture keeps its size.</summary>
        public const int CaptureWidth = 640;
        public const int CaptureHeight = 360;
        /// <summary>The JPEG quality (0.7 on the 0 to 1 scale of the web SDK).</summary>
        public const int JpegQuality = 70;
        /// <summary>The quality of the second encoding when the first JPEG is above MaxFrameBytes.</summary>
        public const int SmallerJpegQuality = 50;
        /// <summary>The shortest time between two frames of one conversation.</summary>
        public const int MinFrameIntervalMs = 1000;
        /// <summary>The default longest time between two frames.</summary>
        public const int IntervalMs = 5000;
        /// <summary>The time between two motion checks.</summary>
        public const int CheckMs = 250;
        /// <summary>The default mean change of the luma grid that sends a frame, 0 to 255 (about 3 %).</summary>
        public const float MotionThreshold = 8f;
        /// <summary>The server answers when the observation is stored, often after 1.3 to 2 s.</summary>
        public const int AckTimeoutMs = 20000;
        /// <summary>The motion grid: 32 x 18 cells, the 16:9 shape of a frame.</summary>
        public const int GridWidth = 32;
        public const int GridHeight = 18;
    }

    /// <summary>
    /// The codes of sight: the refusals of the server, the codes of the conversation connection,
    /// and the codes of the SDK. Another code is a code of a newer server.
    /// </summary>
    public static class NpcifySightCodes
    {
        public const string Disabled = "SIGHT_DISABLED";
        public const string RateLimited = "SIGHT_RATE_LIMITED";
        public const string FrameTooLarge = "SIGHT_FRAME_TOO_LARGE";
        public const string FrameInvalid = "SIGHT_FRAME_INVALID";
        public const string ProviderFailed = "SIGHT_PROVIDER_FAILED";
        public const string InsufficientPoints = "INSUFFICIENT_POINTS";
        public const string AccessDenied = "CONVERSATION_ACCESS_DENIED";
        public const string Disconnected = "CONVERSATION_DISCONNECTED";
        public const string Timeout = "CONVERSATION_TIMEOUT";
        public const string ConversationRequired = "CONVERSATION_REQUIRED";
        public const string AlreadyStarted = "SIGHT_ALREADY_STARTED";
        public const string CameraPermissionDenied = "CAMERA_PERMISSION_DENIED";
        public const string CameraUnavailable = "CAMERA_UNAVAILABLE";
        /// <summary>The game view cannot be read: no camera, texture or GPU readback for it.</summary>
        public const string SourceUnavailable = "SIGHT_SOURCE_UNAVAILABLE";
        /// <summary>The source ended: a destroyed camera or texture, or a webcam that stopped.</summary>
        public const string SourceEnded = "SIGHT_SOURCE_ENDED";

        public const string OffMessage = "Sight is off for this character. Its owner turns it on in the Sight tab.";

        /// <summary>The refusals that stop sight. The SDK skips the frame for other codes.</summary>
        public static bool StopsSight(string code)
        {
            return code == Disabled || code == InsufficientPoints || code == AccessDenied;
        }

        /// <summary>A message for a code.</summary>
        public static string Message(string code)
        {
            switch (code)
            {
                case Disabled:
                    return OffMessage;
                case InsufficientPoints:
                    return "The owner of the character has no points to pay for sight.";
                case AccessDenied:
                    return "This connection has no access to the conversation.";
                case Disconnected:
                    return "The conversation connection is not open.";
                case Timeout:
                    return "The server did not answer in time.";
                case ConversationRequired:
                    return "Create a conversation before sight starts.";
                case AlreadyStarted:
                    return "Sight has already started.";
                case CameraPermissionDenied:
                    return "Camera access was denied.";
                case CameraUnavailable:
                    return "No camera is available.";
                case SourceUnavailable:
                    return "The sight source cannot be read.";
                case SourceEnded:
                    return "The video of sight ended.";
                default:
                    return code;
            }
        }
    }

    /// <summary>A failure of sight with its code (<see cref="NpcifySightCodes"/>).</summary>
    public sealed class NpcifySightException : Exception
    {
        public NpcifySightException(string code, string message = null)
            : base(message ?? NpcifySightCodes.Message(code))
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>The schedule of sight. The engine part adds the source (a camera, a texture or a webcam).</summary>
    public sealed partial class NpcifySightOptions
    {
        /// <summary>The longest time between two frames. Default 5000 ms; the minimum is 1000 ms.</summary>
        public int IntervalMs { get; set; } = NpcifySightLimits.IntervalMs;

        /// <summary>A change of the picture sends a frame at once (one each second at most). False: only the interval sends frames.</summary>
        public bool Motion { get; set; } = true;

        /// <summary>The mean change of the 32 x 18 luma grid between two checks that counts as motion, 0 to 255 (default 8).</summary>
        public float MotionThreshold { get; set; } = NpcifySightLimits.MotionThreshold;
    }

    /// <summary>One JPEG frame of a source.</summary>
    public sealed class NpcifySightFrame
    {
        public byte[] Jpeg;
        public int Width;
        public int Height;
    }

    /// <summary>The body of <c>conversations:sight</c>.</summary>
    public sealed class NpcifySightFrameRequest
    {
        public string ConversationId;
        /// <summary>The capture time in Unix milliseconds.</summary>
        public long CapturedAt;
        public int Width;
        public int Height;
        /// <summary>"game" for a render of the game, "camera" for a webcam.</summary>
        public string Source;
        public byte[] Jpeg;

        /// <summary>The JSON of the body, with the Socket.IO placeholder of the JPEG attachment.</summary>
        public string ToJson()
        {
            return "{\"conversationId\":" + NpcifyJsonText.String(ConversationId) +
                   ",\"capturedAt\":" + CapturedAt.ToString(CultureInfo.InvariantCulture) +
                   ",\"width\":" + Width.ToString(CultureInfo.InvariantCulture) +
                   ",\"height\":" + Height.ToString(CultureInfo.InvariantCulture) +
                   ",\"source\":" + NpcifyJsonText.String(Source) +
                   ",\"jpeg\":{\"_placeholder\":true,\"num\":0}}";
        }
    }

    /// <summary>
    /// The result of one frame: the acknowledgement of the server (Answered), or the code of the
    /// conversation connection when no acknowledgement came (CONVERSATION_TIMEOUT,
    /// CONVERSATION_DISCONNECTED or CONVERSATION_ACCESS_DENIED).
    /// </summary>
    public sealed class NpcifySightAck
    {
        public bool Answered;
        public bool Ok;
        public string ObservationId;
        /// <summary>The code of a refused frame or of the connection; null when Ok.</summary>
        public string Error;

        public static NpcifySightAck Accepted(string observationId)
        {
            return new NpcifySightAck { Answered = true, Ok = true, ObservationId = observationId };
        }

        public static NpcifySightAck Refused(string code)
        {
            return new NpcifySightAck { Answered = true, Ok = false, Error = code };
        }

        public static NpcifySightAck NotAnswered(string code)
        {
            return new NpcifySightAck { Answered = false, Ok = false, Error = code };
        }

        /// <summary>The acknowledgement of a Socket.IO ack packet (the JSON array of its arguments), or null.</summary>
        public static NpcifySightAck FromAckJson(string json)
        {
            var arguments = NpcifyJson.TryParse(json) as List<object>;
            var body = arguments != null && arguments.Count > 0 ? arguments[0] as Dictionary<string, object> : null;
            if (body == null || !body.TryGetValue("ok", out var ok) || !(ok is bool))
            {
                return Refused(NpcifySightCodes.FrameInvalid);
            }
            if ((bool)ok)
            {
                return Accepted(body.TryGetValue("observationId", out var id) ? id as string : null);
            }
            return Refused(body.TryGetValue("error", out var error) && error is string code && code.Length > 0
                ? code
                : NpcifySightCodes.FrameInvalid);
        }
    }

    /// <summary>One person or avatar in a frame.</summary>
    public sealed class NpcifySightPerson
    {
        /// <summary>"left", "center" or "right".</summary>
        public string Position;
        /// <summary>What the person does, in five words or fewer.</summary>
        public string Action;
        /// <summary>The person faces or addresses the camera.</summary>
        public bool FacingCamera;
    }

    /// <summary>
    /// An observation of the conversation (<c>sight_observation</c>): what the vision model saw in
    /// one frame. The character reads it in its next reply.
    /// </summary>
    public sealed class NpcifySightObservation
    {
        public string ConversationId;
        public string ObservationId;
        /// <summary>The capture time that the client sent, as an ISO 8601 string.</summary>
        public string CapturedAt;
        /// <summary>The number of people or avatars in the frame.</summary>
        public int People;
        public List<NpcifySightPerson> Persons = new List<NpcifySightPerson>();
        /// <summary>Each readable piece of text, verbatim.</summary>
        public List<string> Text = new List<string>();
        /// <summary>One sentence about the frame, for the character.</summary>
        public string Summary;
        public string Model;
        /// <summary>The points charged to the owner; null while the charge is pending.</summary>
        public double? CostPoints;

        /// <summary>
        /// The observation of a sight_observation payload (its JSON object), or null when a field
        /// that the SDK reads is missing.
        /// </summary>
        public static NpcifySightObservation FromJson(string json)
        {
            return FromMap(NpcifyJson.TryParse(json) as Dictionary<string, object>);
        }

        public static NpcifySightObservation FromMap(Dictionary<string, object> map)
        {
            if (map == null ||
                !(Field(map, "conversationId") is string conversationId) ||
                !(Field(map, "observationId") is string observationId) ||
                !(Field(map, "summary") is string summary) ||
                !(Field(map, "observation") is Dictionary<string, object> observation) ||
                !(Field(observation, "people") is double people) ||
                !(Field(observation, "persons") is List<object> persons) ||
                !(Field(observation, "text") is List<object> text))
            {
                return null;
            }
            var result = new NpcifySightObservation
            {
                ConversationId = conversationId,
                ObservationId = observationId,
                CapturedAt = Field(map, "capturedAt") as string,
                People = (int)Math.Max(0, Math.Min(int.MaxValue, people)),
                Summary = summary,
                Model = Field(map, "model") as string,
                CostPoints = Field(map, "costPoints") is double points ? points : (double?)null,
            };
            foreach (var entry in persons)
            {
                if (entry is Dictionary<string, object> person)
                {
                    result.Persons.Add(new NpcifySightPerson
                    {
                        Position = Field(person, "position") as string,
                        Action = Field(person, "action") as string,
                        FacingCamera = Field(person, "facingCamera") is bool facing && facing,
                    });
                }
            }
            foreach (var entry in text)
            {
                if (entry is string line)
                {
                    result.Text.Add(line);
                }
            }
            return result;
        }

        private static object Field(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>JSON text of the values that the SDK sends.</summary>
    internal static class NpcifyJsonText
    {
        public static string String(string value)
        {
            return value == null ? "null" : Api.NpcifySocketIo.JsonString(value);
        }
    }
}
