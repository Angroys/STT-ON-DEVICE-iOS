# On-Device Speech-to-Text for Unity (Vosk)

Real-time, offline speech recognition for Unity using [Vosk](https://alphacep.com/). Works on macOS, Windows, Android, and iOS. No internet connection required — all inference runs on-device.

---

## Table of Contents

- [How It Works](#how-it-works)
- [Project Structure](#project-structure)
- [Integrating Into Your Own Project](#integrating-into-your-own-project)
  - [1. Copy the Required Files](#1-copy-the-required-files)
  - [2. Add a Vosk Language Model](#2-add-a-vosk-language-model)
  - [3. Set Up GameObjects in Your Scene](#3-set-up-gameobjects-in-your-scene)
  - [4. Configure the Inspector](#4-configure-the-inspector)
  - [5. Subscribe to Transcription Results in Code](#5-subscribe-to-transcription-results-in-code)
- [Platform-Specific Setup](#platform-specific-setup)
  - [macOS / Unity Editor](#macos--unity-editor)
  - [iOS](#ios)
  - [Android](#android)
  - [Windows](#windows)
- [Component Reference](#component-reference)
  - [VoiceProcessor](#voiceprocessor)
  - [VoskSpeechToText](#voskspeechtotext)
  - [RecognitionResult / RecognizedPhrase](#recognitionresult--recognizedphrase)
- [Key Bugs Fixed in This Fork](#key-bugs-fixed-in-this-fork)
- [Troubleshooting](#troubleshooting)

---

## How It Works

```
Microphone (device native rate)
        │
        ▼
  VoiceProcessor          — records audio, resamples to 16 kHz, fires OnFrameCaptured
        │  short[] frames (16 kHz PCM)
        ▼
  VoskSpeechToText        — enqueues frames into a thread-safe queue
        │
        ▼
  Background thread       — feeds frames to VoskRecognizer.AcceptWaveform()
        │  JSON result strings
        ▼
  Unity main thread       — dequeues results in Update(), fires OnTranscriptionResult
        │  string (JSON)
        ▼
  Your code               — parse with RecognitionResult, act on text
```

The recognizer runs entirely on a background `Task` thread to avoid blocking the Unity main thread. Results are marshalled back via a `ConcurrentQueue` polled in `Update()`.

---

## Project Structure

```
Assets/
├── Scripts/
│   ├── VoiceProcessor.cs       # Microphone capture + resampling
│   ├── VoskSpeechToText.cs     # Main STT controller
│   ├── RecognitionResult.cs    # JSON result parser
│   ├── VoskResultText.cs       # Example: display raw transcription in UI Text
│   └── VoskDialogText.cs       # Example: dialog game using speech commands
└── ThirdParty/
    ├── Vosk/                   # Vosk C# bindings + native libraries
    │   ├── Vosk.cs
    │   ├── VoskRecognizer.cs
    │   ├── Model.cs
    │   ├── VoskPINVOKE.cs
    │   └── (platform native libs: .dylib, .so, .dll, .a)
    ├── SimpleJson/             # SimpleJSON parser (Bunny83)
    │   └── SimpleJSON.cs
    └── Zip/                    # DotNetZip (Ionic.Zip) for model decompression
```

---

## Integrating Into Your Own Project

### 1. Copy the Required Files

Copy these folders from `Assets/` into your project's `Assets/` folder:

```
Assets/Scripts/VoiceProcessor.cs
Assets/Scripts/VoskSpeechToText.cs
Assets/Scripts/RecognitionResult.cs
Assets/ThirdParty/Vosk/          ← entire folder (bindings + native libs)
Assets/ThirdParty/SimpleJson/    ← entire folder
Assets/ThirdParty/Zip/           ← entire folder
```

You do **not** need `VoskDialogText.cs` or `VoskResultText.cs` — those are demo scripts.

> **Important:** The `ThirdParty/Vosk/` folder contains platform-specific native libraries (`.dylib` for macOS, `.so` for Android/Linux, `.dll` for Windows, `.a` for iOS). Make sure you copy all of them and that their Unity Platform settings (in the Inspector) are configured correctly for each target platform.

### 2. Add a Vosk Language Model

1. Download a model for your language from [https://alphacep.com/models.html](https://alphacep.com/models.html).
   Choose a **"small"** model for mobile/real-time use. The Russian small model (`vosk-model-small-ru-0.22`) is ~40 MB.

2. Place the `.zip` file in `Assets/StreamingAssets/`:
   ```
   Assets/StreamingAssets/vosk-model-small-en-us-0.15.zip   ← example for English
   ```

3. On first run, VoskSpeechToText will automatically decompress the zip into `Application.persistentDataPath`. On subsequent runs it detects the already-decompressed folder and skips extraction.

> If you want to ship a pre-extracted model (e.g. for iOS, where zip extraction at runtime can be slow), place the **unzipped model folder** directly in `StreamingAssets/` and set `ModelPath` to the folder name (no `.zip` extension).

### 3. Set Up GameObjects in Your Scene

You need two GameObjects:

**GameObject 1 — VoiceProcessor**

1. Create an empty GameObject, name it `VoiceProcessor`.
2. Add the `VoiceProcessor` component.
3. Ensure your scene has exactly **one** `AudioListener` (normally on the Main Camera). Multiple AudioListeners cause Unity warnings and can interfere with audio capture.

**GameObject 2 — VoskSpeechToText**

1. Create an empty GameObject, name it `VoskSpeechToText`.
2. Add the `VoskSpeechToText` component.
3. In the Inspector, set `Voice Processor` to the GameObject from step above.
4. Set `Model Path` to your model's filename in StreamingAssets (e.g. `vosk-model-small-en-us-0.15.zip`).

### 4. Configure the Inspector

**VoiceProcessor Inspector fields:**

| Field | Default | Description |
|---|---|---|
| Microphone Index | 0 | Index into `Microphone.devices[]`. Change in Editor to switch microphone. |
| Minimum Speaking Sample Value | 0.05 | Volume threshold for voice-activity detection (only used when Auto Detect is on). |
| Silence Timer | 1.0 s | Seconds of silence before `OnRecordingStop` fires (Auto Detect mode only). |
| Auto Detect | false | When **off**: every audio frame is forwarded to Vosk (continuous recognition). When **on**: only forwards audio above the volume threshold, fires `OnRecordingStop` after silence. |

**VoskSpeechToText Inspector fields:**

| Field | Default | Description |
|---|---|---|
| Model Path | `vosk-model-small-ru-0.22.zip` | Path relative to `StreamingAssets/`. |
| Voice Processor | — | Drag your VoiceProcessor GameObject here. |
| Max Alternatives | 3 | Number of alternative transcription hypotheses returned per result. |
| Max Record Length | 5 | (Currently unused — available for future per-utterance splitting.) |
| Auto Start | true | Call `StartVoskStt()` automatically on `Start()`. |
| Key Phrases | _(empty)_ | If set, only these words/phrases are recognised (others become `[unk]`). Leave empty to recognise all words. |

### 5. Subscribe to Transcription Results in Code

```csharp
using UnityEngine;

public class MyTranscriptionHandler : MonoBehaviour
{
    public VoskSpeechToText voskStt;

    void Awake()
    {
        voskStt.OnTranscriptionResult += OnResult;
        voskStt.OnStatusUpdated += OnStatus;
    }

    void OnDestroy()
    {
        voskStt.OnTranscriptionResult -= OnResult;
    }

    private void OnResult(string json)
    {
        var result = new RecognitionResult(json);

        // result.Phrases is sorted by confidence (highest first)
        // result.Partial is true for partial/interim results
        foreach (RecognizedPhrase phrase in result.Phrases)
        {
            if (!string.IsNullOrEmpty(phrase.Text))
            {
                Debug.Log($"Recognised: {phrase.Text}  (confidence: {phrase.Confidence})");
                // Do something with phrase.Text
            }
        }
    }

    private void OnStatus(string status)
    {
        Debug.Log($"Vosk status: {status}");
    }
}
```

**Starting and stopping recognition manually** (when `AutoStart = false`):

```csharp
// Start with a custom model and keyword list
voskStt.StartVoskStt(
    keyPhrases: new List<string> { "hello", "stop", "go left", "go right" },
    modelPath: "vosk-model-small-en-us-0.15.zip",
    startMicrophone: false,
    maxAlternatives: 1
);

// Toggle mic on/off at runtime
voskStt.ToggleRecording();
```

---

## Platform-Specific Setup

### macOS / Unity Editor

- **Microphone permission:** macOS requires explicit permission for each app to access the microphone. In the Unity Editor, go to **System Settings → Privacy & Security → Microphone** and enable **Unity**. You must restart the Editor after granting permission.

- **Sample rate:** Some audio devices (e.g. AirPods) only support a single sample rate (e.g. 24000 Hz) that differs from Vosk's required 16000 Hz. `VoiceProcessor` handles this automatically — it queries `Microphone.GetDeviceCaps`, records at the device's native rate, and resamples to 16000 Hz via linear interpolation before forwarding to Vosk.

- **Text-to-speech feedback:** `VoskDialogText` uses `System.Diagnostics.Process.Start("/usr/bin/say", …)` for audio feedback. This works only on macOS and is compiled out on other platforms via `#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX`.

### iOS

1. **Native library:** Ensure `Assets/ThirdParty/Vosk/` contains a fat static library (`.a`) built for `arm64` (device) and, optionally, `x86_64` (simulator). In Unity's Plugin Inspector, set the platform to **iOS** only.

2. **Microphone permission:** Add the `NSMicrophoneUsageDescription` key to your `Info.plist`. In Unity, go to **Player Settings → iOS → Other Settings → Microphone Usage Description** and enter a user-facing reason string (e.g. `"This app uses the microphone for voice recognition."`).

3. **No `System.Diagnostics.Process`:** `Process.Start` is not available on iOS. All calls in this project are guarded with `#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX` and will compile safely.

4. **Model size:** iOS App Store has a 4 GB limit. The small Vosk models (~40–200 MB) fit comfortably. Ship the model pre-extracted in `StreamingAssets/` to avoid runtime zip extraction overhead on first launch.

5. **IL2CPP:** The project uses IL2CPP on iOS. Vosk's P/Invoke bindings (`VoskPINVOKE.cs`) rely on `DllImport`. Ensure the native library name matches exactly: `[DllImport("vosk")]` requires `libvosk.a` to be present and linked by Xcode.

### Android

1. **Native library:** `Assets/ThirdParty/Vosk/` should contain `libvosk.so` binaries for each ABI you support (`arm64-v8a`, `armeabi-v7a`, `x86_64`). Set their platform in the Unity Inspector to **Android**.

2. **Microphone permission:** Add `RECORD_AUDIO` to your Android manifest. In Unity: **Player Settings → Android → Other Settings → Internet Access** (set to Required) and ensure `AndroidManifest.xml` includes:
   ```xml
   <uses-permission android:name="android.permission.RECORD_AUDIO" />
   ```
   At runtime, Unity's `Microphone.Start` will automatically trigger the permission request on Android 6+.

3. **StreamingAssets on Android:** Files in `StreamingAssets/` on Android are inside the APK and must be read via `UnityWebRequest` (not `File.OpenRead`). `VoskSpeechToText` already handles this — it checks if the path contains `://` and uses `UnityWebRequest` for Android/WebGL paths.

### Windows

- Native library: `vosk.dll` must be present and set to platform **Windows** in the Inspector.
- No additional configuration required.

---

## Component Reference

### VoiceProcessor

Handles microphone input and delivers resampled PCM frames.

**Key public API:**

```csharp
// Start capturing. SampleRate is the OUTPUT rate (Vosk expects 16000).
// The component automatically records at the device's native rate and resamples.
void StartRecording(int sampleRate = 16000, int frameSize = 512, bool? autoDetect = null)

void StopRecording()

void ChangeDevice(int deviceIndex)   // hot-swap microphone
void UpdateDevices()                 // refresh Devices list

bool IsRecording          { get; }
int  SampleRate           { get; }   // output sample rate (16000)
int  FrameLength          { get; }   // output frame size in samples
List<string> Devices      { get; }
int  CurrentDeviceIndex   { get; }
string CurrentDeviceName  { get; }

event Action<short[]> OnFrameCaptured   // fired each frame of 16 kHz PCM
event Action OnRecordingStart
event Action OnRecordingStop            // fired on silence (autoDetect=true) or StopRecording()
```

**Sample rate handling:** `VoiceProcessor` queries `Microphone.GetDeviceCaps` and always records at `maxFreq` (the device's highest/only supported rate). If this differs from `sampleRate`, frames are downsampled via linear interpolation before `OnFrameCaptured` fires. This is transparent to callers — `OnFrameCaptured` always delivers frames at `SampleRate` (16000 Hz).

### VoskSpeechToText

Main controller. Loads the model, wires up `VoiceProcessor`, and manages the background recognizer thread.

**Key public API:**

```csharp
void StartVoskStt(
    List<string> keyPhrases = null,
    string modelPath = default,
    bool startMicrophone = false,
    int maxAlternatives = 3)

void ToggleRecording()   // start or stop microphone + recognizer

event Action<string> OnTranscriptionResult   // JSON string, parse with RecognitionResult
event Action<string> OnStatusUpdated         // human-readable status messages
```

### RecognitionResult / RecognizedPhrase

Parse the JSON string delivered by `OnTranscriptionResult`:

```csharp
public class RecognitionResult
{
    public RecognizedPhrase[] Phrases;  // alternatives, highest confidence first
    public bool Partial;                // true for interim/partial results
}

public class RecognizedPhrase
{
    public string Text;        // transcribed text (trimmed)
    public float Confidence;   // log probability (higher = more confident, e.g. -0.5 > -10.0)
}
```

`Phrases[0]` is always the highest-confidence alternative. Check `!string.IsNullOrEmpty(Phrases[0].Text)` before acting on a result.

---

## Key Bugs Fixed in This Fork

The following bugs from the original upstream code are fixed in this repository:

| Bug | File | Description |
|---|---|---|
| `Buffer.BlockCopy` byte/element mismatch | `VoiceProcessor.cs` | `BlockCopy` takes **byte** counts. Copying `float[]` requires `count * sizeof(float)`. The original passed element counts, copying ¼ of the data and writing to the wrong offset. |
| `StopCoroutine` had no effect | `VoiceProcessor.cs` | `StopCoroutine(RecordData())` creates a *new* coroutine instance and stops it immediately — the running coroutine is unaffected. Fixed by storing the `Coroutine` reference from `StartCoroutine` and stopping by reference. |
| Last utterance dropped on stop | `VoskSpeechToText.cs` | When `ToggleRecording()` sets `_running = false`, audio buffered inside the Vosk recognizer was silently discarded. Fixed by calling `_recognizer.FinalResult()` after the `while (_running)` loop exits. |
| `Process.Start` crashes on iOS | `VoskDialogText.cs` | `System.Diagnostics.Process.Start` does not exist on iOS. Fixed with `#if UNITY_STANDALONE_OSX \|\| UNITY_EDITOR_OSX`. |
| Device sample rate mismatch | `VoiceProcessor.cs` | When a microphone only supports a rate other than 16000 Hz (e.g. AirPods at 24000 Hz), Unity recorded at that rate but labelled the `AudioClip` as 16000 Hz. All position arithmetic was then wrong, producing corrupted/silent audio. Fixed by recording at `maxFreq` and resampling with linear interpolation. |

---

## Troubleshooting

**All transcription results have empty `text`**

1. Check that the correct language model is loaded — the default model is Russian (`vosk-model-small-ru-0.22`). If you are speaking English, download and use an English model.
2. Check the Unity Console for `[VoiceProcessor]` log output. It shows the device name, requested output rate, and actual recording rate. If it says `Recording at: 24000 Hz` but `Output: 16000 Hz`, resampling is active — that is normal and correct.
3. On macOS, verify that Unity Editor has microphone permission in **System Settings → Privacy & Security → Microphone**.

**`[Vosk] Microphone appears to be returning silence`**

The OS is not granting microphone access. Grant permission to Unity (macOS) or check `RECORD_AUDIO` permission at runtime (Android).

**No output at all (no results, no logs)**

- Confirm `Auto Start` is checked on `VoskSpeechToText`, or that you call `StartVoskStt()` manually.
- Confirm the `VoiceProcessor` reference is assigned in the `VoskSpeechToText` Inspector.
- Check the Console for model loading errors — the model path must match the filename in `StreamingAssets/` exactly (case-sensitive on macOS/Linux).

**Recognition is slow or delayed**

- Use a "small" model. The "large" models are more accurate but too slow for real-time use on mobile hardware.
- Ensure `_autoDetect` is `false` on `VoiceProcessor` for continuous streaming. When `true`, the voice-activity threshold delays the start of forwarding audio.

**Multiple AudioListener warning**

Unity allows only one `AudioListener` per scene. Remove any extra `AudioListener` components (check all Camera GameObjects).

**iOS build fails with missing symbol `vosk_…`**

The static library `libvosk.a` must be present in `ThirdParty/Vosk/` and its Unity Plugin Inspector must have **iOS** checked. Make sure you are not accidentally including a macOS `.dylib` in the iOS build.
