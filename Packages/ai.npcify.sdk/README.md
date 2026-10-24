# Npcify Unity SDK (`ai.npcify.sdk`)

Unity Package Manager (UPM) client for embedding Npcify AI characters into Unity 2022.3+ and Unity 6 3D games.

## Features

- **NPCP v1.0 Binary Performance Codec (`NpcifyPerformanceCodec`, `NpcifyMsgPack`)**: Zero-dependency C# implementation of the 24-byte NPCP envelope, IEEE 802.3 CRC-32 validation, bounded GZip decompression, and strict MessagePack wire subset validation.
- **Offline Baked Conversations (`NpcifyBakedConversation`)**: Load `.npcp` baked conversation assets (`npcify.baked-conversation`) with embedded WAV/PCM audio, cycle/parent/option validation, and instant offline branching (`Start()`, `Select()`, `Reply()`, `Worldline()`).
- **Live HTTP & NPCP API Client (`NpcifyApiClient`)**: Scoped API key authentication (`Authorization: Bearer` and `X-API-Token`), VRM/VRMA signed URL resolution, conversation creation, and staging generation with `Idempotency-Key` retries on HTTP `409`/`502`/`503`/`504`.
- **Layered Facial Expression & Viseme Mixer (`NpcifyFacialMixer`, `NpcifyVisemePlayer`)**: Multi-layer blending (`Emotion`, `Viseme`, `Blink`, `Gaze`), exponential smoothing, automatic natural blinking, coarticulated viseme scheduling, and automatic alias resolution across VRM 1.0 (`aa`, `ih`, `ou`, `ee`, `oh`, `happy`), VRM 0.x (`A`, `I`, `U`, `E`, `O`, `Joy`), and ARKit/Fcl blendshapes.
- **Unity `MonoBehaviour` Integration (`NpcifyCharacter`)**:
  - Real-time access to `Root`, `Animator`, `SkinnedMeshRenderers`, `Meshes`, `Materials`, and humanoid bones via `GetBone(HumanBodyBones)`.
  - Spatial 3D `AudioSource` playback with automatic WAV/PCM16/Float32 `AudioClip` construction and live RMS amplitude analysis.
  - Real-time `Speech` state (`Speaking`, `Thinking`, `CurrentSpeech`, `CurrentText`, `RevealedText`, `Options`, `Cursor`) and `Expressions.MouthOpenness`.
  - Custom Unity Editor Inspector (`NpcifyCharacterEditor`) to import `npcify-unity-sdk.json` exported from the Npcify dashboard and auto-bind avatar rigs.

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
    "ai.npcify.sdk": "0.2.0"
  }
}
```

### Option 2: Download `.tgz` Tarball

Download the latest UPM package tarball from `https://npcify.ai/sdk/unity/latest` (or a specific version from `https://npcify.ai/sdk/unity/app/0.2.0`), then in Unity open **Window > Package Manager > + > Install package from tarball...** and select the downloaded `ai.npcify.sdk-0.2.0.tgz` file.

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

## Example Project

[midaikidev/npcify-example-unity](https://github.com/midaikidev/npcify-example-unity)
is a third-person game with a baked offline NPC and an online NPC. It shows
runtime VRM loading with UniVRM, VRMA animations and an RPG dialogue UI.

## Running Tests

To run the standalone C# protocol, baked conversation, facial mixer, timeline player, and API client tests against the repository's golden NPCP compatibility fixtures:

```bash
docker run --rm -v "$(pwd):/workspace" -w /workspace/clients/unity-client/Tests~ mcr.microsoft.com/dotnet/sdk:8.0 dotnet run -- /workspace
```
