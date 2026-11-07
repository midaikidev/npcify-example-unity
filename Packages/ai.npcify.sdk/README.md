# Npcify Unity SDK (`ai.npcify.sdk`)

Unity Package Manager (UPM) client for embedding Npcify AI characters into Unity 2022.3+ and Unity 6 3D games.

## Features

- **NPCP v1.0 Binary Performance Codec (`NpcifyPerformanceCodec`, `NpcifyMsgPack`)**: Zero-dependency C# implementation of the 24-byte NPCP envelope, IEEE 802.3 CRC-32 validation, bounded GZip decompression, and strict MessagePack wire subset validation.
- **Offline Baked Conversations (`NpcifyBakedConversation`)**: Load `.npcp` baked conversation assets (`npcify.baked-conversation`) with embedded WAV/PCM audio, cycle/parent/option validation, and instant offline branching (`Start()`, `Select()`, `Reply()`, `Worldline()`).
- **Live HTTP & NPCP API Client (`NpcifyApiClient`)**: Scoped API key authentication (`Authorization: Bearer` and `X-API-Token`), VRM/VRMA signed URL resolution, conversation creation, and staging generation with `Idempotency-Key` retries on HTTP `409`/`502`/`503`/`504`.
- **Layered Facial Expression & Viseme Mixer (`NpcifyFacialMixer`, `NpcifyVisemePlayer`)**: Multi-layer blending (`Emotion`, `Viseme`, `Blink`, `Gaze`), exponential smoothing, automatic natural blinking, coarticulated viseme scheduling, and automatic alias resolution across VRM 1.0 (`aa`, `ih`, `ou`, `ee`, `oh`, `happy`), VRM 0.x (`A`, `I`, `U`, `E`, `O`, `Joy`), and ARKit/Fcl blendshapes.
- **Unity `MonoBehaviour` Integration (`NpcifyCharacter`)**:
  - Real-time access to `Root`, `Animator`, `SkinnedMeshRenderers`, `Meshes`, `Materials`, and humanoid bones via `GetBone(HumanBodyBones)`.
  - Spatial 3D `AudioSource` playback with automatic WAV/PCM16/Float32 `AudioClip` construction and live RMS amplitude analysis. See [Spatial voice](#spatial-voice).
  - Real-time `Speech` state (`Speaking`, `Thinking`, `CurrentSpeech`, `CurrentText`, `RevealedText`, `Options`, `Cursor`) and `Expressions.MouthOpenness`.
  - Custom Unity Editor Inspector (`NpcifyCharacterEditor`) to import `npcify-unity-sdk.json` exported from the Npcify dashboard and auto-bind avatar rigs.
  - Voice input: push-to-talk key or UI button, `StartListening` / `StopListeningAsync` / `CancelListening`, and the `InputModes` configuration. See [Voice input](#voice-input).
  - Live motion: the generated body motion of each reply on a UniVRM 1.0 model, and `PrepareLiveMotionAsync()`. See [Live motion](#live-motion).

## Installation

### Option 1: UPM Scoped Registry (`Packages/manifest.json`)

Add the Npcify scoped registry and `ai.npcify.sdk` to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "Npcify",
      "url": "https://npcify.ai/sdk/unity/registry",
      "scopes": ["ai.npcify"]
    }
  ],
  "dependencies": {
    "ai.npcify.sdk": "0.5.0"
  }
}
```

### Option 2: Download `.tgz` Tarball

Download the latest UPM package tarball from `https://npcify.ai/sdk/unity/latest` (or a specific version from `https://npcify.ai/sdk/unity/app/0.5.0`), then in Unity open **Window > Package Manager > + > Install package from tarball...** and select the downloaded `ai.npcify.sdk-0.5.0.tgz` file.

## Quick Start (3D Game Integration)

```csharp
using Npcify.Sdk.Models;
using Npcify.Sdk.Unity;
using UnityEngine;

public sealed class NpcifyGameExample : MonoBehaviour
{
    [SerializeField] private NpcifyCharacter character;
    [SerializeField] private Transform playerCamera;
    [SerializeField] private Transform propPrefab;

    private async void Start()
    {
        character.Configure(new NpcifyOptions
        {
            BaseUrl = "https://api.npcify.ai",
            CharacterId = "<character-id>",
            ApiKey = "<paste-your-scoped-api-key>",
        });

        character.RefreshAvatarBindings();
        character.LookAt(playerCamera);

        var rightHand = character.GetBone(HumanBodyBones.RightHand);
        if (rightHand != null && propPrefab != null)
        {
            Instantiate(propPrefab, rightHand, false);
        }

        character.OnCharacterRevealed += (revealedText, cue) =>
        {
            Debug.Log($"Subtitle: {revealedText}");
        };

        character.SetEmotion("happy", 0.6f);
        character.PlayAnimation("Greeting", loop: false);
        await character.SpeakAsync("Welcome to the realm! How can I help you?");
    }

    private void Update()
    {
        var openness = character.Expressions.MouthOpenness;
        var speaking = character.Speech.Speaking;
    }
}
```

## Speech Audio, Threading and Conversations

- Speech audio arrives as MPEG bytes (baked conversations) or as a signed URL
  (live stagings). `NpcifyCharacter` loads both with `UnityWebRequestMultimedia`
  before the staging starts, so audio, visemes and the typewriter text start
  together. WAV and raw PCM bytes still decode in C#.
- The async methods (`CreateConversationAsync`, `SpeakAsync`, `ReplyAsync`,
  `StartBakedConversationAsync`) resume on the caller's synchronization context,
  that is the Unity main thread.
- `AttachBakedConversation(byte[])` loads a baked `.npcp` asset at run time, for
  example from `StreamingAssets`. `ResetConversation()` stops speech and starts
  the next conversation from the opening.
- `OnAnimationRequested(animationId, loop)` reports the animation cues of a
  staging. Play them with your own animation system (for example UniVRM
  `Vrm10AnimationInstance` for the character's VRMA files). An `Animator` with a
  controller that has a state of that name also plays it.

## Voice Input

The player can talk to the character when its owner turned voice input on
(the STT tab of the character in the dashboard; see
[voice input](../../docs/voice-input.md)). The microphone records while the
player holds a key or a button; at the release the recording goes to the
server, and its text becomes the player's message. The runtime reads the
character's info once before the conversation starts; with voice input off,
listening throws `NpcifyInputModeException` and nothing is recorded or sent.

- **Trigger:** set `PushToTalkKey` in the Inspector (legacy Input Manager), add
  `NpcifyPushToTalkButton` to a UI element (needs `com.unity.ugui`), or call
  `BeginPushToTalk()` and `EndPushToTalk()` from your own input, for example an
  Input System action. A press shorter than 0.3 s, or a release before the
  microphone gave any audio (a permission prompt, a slow device), sends
  nothing and raises `PushToTalkShortPress`: show "hold to talk". A release
  after the microphone started sends what it recorded. Focus loss cancels the
  recording.
- **By code:** `StartListening(options)` starts recording and its task gives the
  result when listening ends: `StopListeningAsync()`, `MaxDurationSeconds`
  (30 s at most) or `EndOnSilenceSeconds` (stop after this much silence that
  follows speech). `CancelListening()` discards it. Example: listen when the
  character stops speaking.

  ```csharp
  character.OnSpeechEnd += _ => character.StartListening(new NpcifyListeningOptions
  {
      EndOnSilenceSeconds = 0.8f,
      MaxDurationSeconds = 10f,
  });
  character.TranscriptReceived += text => Debug.Log($"Player: {text}");
  ```

- **Input modes:** `InputModes` (Inspector or `NpcifyOptions.InputModes`) is a
  list of `Options`, `Text` and `Voice`; one mode forces it. Empty: every mode
  that the character permits. A baked conversation permits only `Options`, a
  character without custom replies permits only `Options`, and `Voice` needs
  the character's voice input; another configuration throws
  `NpcifyInputModeException`. With `InputModes` set,
  `SpeakAsync` (typed text) needs `Text`, `ReplyAsync` needs `Options` or
  `Text`, and listening needs `Voice`.
- **Language:** `speechLanguage` is an optional hint (`ja`, `zh`, `en`). Without
  it the server detects the language.
- **Microphone:** the SDK records with `UnityEngine.Microphone` at 16 kHz and asks
  for the permission on the first recording (iOS and Android). Set
  **Player Settings > Microphone Usage Description** for iOS. Unity WebGL has no
  microphone; use the web SDK there.
- **Upload:** at the release the SDK sends one WAV file to
  `POST /conversations/:id/speech`, then the text as a turn. The server charges
  the recognition to the character owner at the provider's price.

## Live Motion

When the owner of a character turns **Live motion** on, the server generates
the body motion of each reply and streams it as pose frames
([real-time motion](../../docs/realtime-motion.md)). The SDK plays them on a
UniVRM 1.0 model; for any other character it keeps the prepared animation
clips.

- **Model:** `NpcifyCharacter` finds the `Vrm10Instance` of its avatar root and
  adds `NpcifyVrm10LiveMotion` (assembly `Npcify.Sdk.Runtime.UniVrm`, compiled
  when the project has `com.vrmc.vrm` 0.131 or later). Load the model with
  `ControlRigGenerationOption.Generate`, and access `Vrm10Instance.Runtime` once
  right after loading, before you rotate or animate the model: UniVRM keeps the
  bone rotations of that moment as the T-pose.
- **Animation:** play idle and other animations as before, as a VRMA
  (`Vrm10Runtime.VrmAnimation`) or with an Animator. The live pose blends over
  them in UniVRM's update (in over 0.3 s, out over 0.5 s), before the spring
  bones.
- **Warm-up:** call `PrepareLiveMotionAsync()` when the player shows that they
  will reply, for example when the reply field gets focus or the player opens
  the conversation with a talk key. Push-to-talk calls it at the press. It runs
  once per conversation and costs the character owner only when the character
  has live motion on.
- **Options:** `liveMotion` (Inspector, or `NpcifyOptions.LiveMotion.Enabled`)
  turns it off; `liveMotionRootMode` `World` keeps the generated hips movement
  (default `Anchored`: the character stays where you put it).
- **State:** `LiveMotion.Weight`, `LiveMotion.GetStats(stagingId)` (frames
  received and played, the staging time of the first played frame),
  `Runtime.OnMotionFrames` and `LiveMotionReady`.
- **Other rigs:** implement `INpcifyLiveMotionTarget` and set
  `LiveMotionTarget`; `NpcifyLiveMotionRig` maps the frames onto your rig from
  its T-pose positions.
- **Transport:** the frames come on the conversation stream (Socket.IO over a
  WebSocket, `System.Net.WebSockets.ClientWebSocket`). WebGL builds have no
  `ClientWebSocket`, so they keep the prepared clips.

## Spatial Voice

The voice is 3D by default (`spatialAudio`): it comes from the character, with
left/right panning and distance falloff. An `AudioSource` that the SDK adds has
linear rolloff, full volume within `voiceMinDistance` (2.5 m) and silence at
`voiceMaxDistance` (30 m). Put your own `AudioSource` on the character to use
other settings; the SDK keeps its distances and rolloff. Turn `spatialAudio` off
for a 2D voice, for example in a menu or a phone call.

Unity hears from the `AudioListener`, usually on the main camera. In a
third-person game with a far camera, you can move the listener to the player
character, so that the distance to the NPC sets the volume.

## Example Project

[midaikidev/npcify-example-unity](https://github.com/midaikidev/npcify-example-unity)
is a third-person game with a baked offline NPC and an online NPC. It shows
runtime VRM loading with UniVRM, VRMA animations and an RPG dialogue UI.

## Running Tests

To run the standalone C# protocol, baked conversation, facial mixer, timeline player, and API client tests against the repository's golden NPCP compatibility fixtures:

```bash
docker run --rm -v "$(pwd):/workspace" -w /workspace/clients/unity-client/Tests~ mcr.microsoft.com/dotnet/sdk:8.0 dotnet run -- /workspace
```
