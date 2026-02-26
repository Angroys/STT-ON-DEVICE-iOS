/* * * * *
 * A unity voice processor
 * ------------------------------
 * 
 * A Unity script for recording and delivering frames of audio for real-time processing
 * 
 * Written by Picovoice 
 * 2021-02-19
 * 
 * Apache License
 * 
 * Copyright (c) 2021 Picovoice
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 *   you may not use this file except in compliance with the License.
 *   You may obtain a copy of the License at
 *   
 *   http://www.apache.org/licenses/LICENSE-2.0
 *   
 *   Unless required by applicable law or agreed to in writing, software
 *   distributed under the License is distributed on an "AS IS" BASIS,
 *   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *   See the License for the specific language governing permissions and
 *   limitations under the License.
 * 
 * * * * */
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// Class that records audio and delivers frames for real-time audio processing
/// </summary>
public class VoiceProcessor : MonoBehaviour
{
    /// <summary>
    /// Indicates whether microphone is capturing or not
    /// </summary>
    public bool IsRecording
    {
        get { return _audioClip != null && Microphone.IsRecording(CurrentDeviceName); }
    }

    [SerializeField] private int MicrophoneIndex;

    /// <summary>
    /// Sample rate of recorded audio
    /// </summary>
    public int SampleRate { get; private set; }

    /// <summary>
    /// Size of audio frames that are delivered
    /// </summary>
    public int FrameLength { get; private set; }

    /// <summary>
    /// Event where frames of audio are delivered
    /// </summary>
    public event Action<short[]> OnFrameCaptured;

    /// <summary>
    /// Event when audio capture thread stops
    /// </summary>
    public event Action OnRecordingStop;

    /// <summary>
    /// Event when audio capture thread starts
    /// </summary>
    public event Action OnRecordingStart;

    /// <summary>
    /// Available audio recording devices
    /// </summary>
    public List<string> Devices { get; private set; }

    /// <summary>
    /// Index of selected audio recording device
    /// </summary>
    public int CurrentDeviceIndex { get; private set; }

    /// <summary>
    /// Name of selected audio recording device
    /// </summary>
    public string CurrentDeviceName
    {
        get
        {
            if (CurrentDeviceIndex < 0 || CurrentDeviceIndex >= Microphone.devices.Length)
                return string.Empty;
            return Devices[CurrentDeviceIndex];
        }
    }

    [Header("Voice Detection Settings")]
    [SerializeField, Tooltip("The minimum volume to detect voice input for"), Range(0.0f, 1.0f)]
    private float _minimumSpeakingSampleValue = 0.05f;

    [SerializeField, Tooltip("Time in seconds of detected silence before voice request is sent")]
    private float _silenceTimer = 1.0f;

    [SerializeField, Tooltip("Auto detect speech using the volume threshold.")]
    private bool _autoDetect;

    private float _timeAtSilenceBegan;
    private bool _audioDetected;
    private bool _didDetect;
    private bool _transmit;


    AudioClip _audioClip;
    private Coroutine _recordDataCoroutine;
    private int _deviceSampleRate;
    private event Action RestartRecording;

    void Awake()
    {
        UpdateDevices();
    }
#if UNITY_EDITOR
    void Update()
    {
        if (CurrentDeviceIndex != MicrophoneIndex)
        {
            ChangeDevice(MicrophoneIndex);
        }
    }
#endif

    /// <summary>
    /// Updates list of available audio devices
    /// </summary>
    public void UpdateDevices()
    {
        Devices = new List<string>();
        foreach (var device in Microphone.devices)
            Devices.Add(device);

        if (Devices == null || Devices.Count == 0)
        {
            CurrentDeviceIndex = -1;
            Debug.LogError("There is no valid recording device connected");
            return;
        }

        CurrentDeviceIndex = MicrophoneIndex;
    }

    /// <summary>
    /// Change audio recording device
    /// </summary>
    /// <param name="deviceIndex">Index of the new audio capture device</param>
    public void ChangeDevice(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= Devices.Count)
        {
            Debug.LogError(string.Format("Specified device index {0} is not a valid recording device", deviceIndex));
            return;
        }

        if (IsRecording)
        {
            // one time event to restart recording with the new device 
            // the moment the last session has completed
            RestartRecording += () =>
            {
                CurrentDeviceIndex = deviceIndex;
                StartRecording(SampleRate, FrameLength);
                RestartRecording = null;
            };
            StopRecording();
        }
        else
        {
            CurrentDeviceIndex = deviceIndex;
        }
    }

    /// <summary>
    /// Start recording audio
    /// </summary>
    /// <param name="sampleRate">Sample rate to record at</param>
    /// <param name="frameSize">Size of audio frames to be delivered</param>
    /// <param name="autoDetect">Should the audio continuously record based on the volume</param>
    public void StartRecording(int sampleRate = 16000, int frameSize = 512, bool ?autoDetect = null)
    {
        if (autoDetect != null)
        {
            _autoDetect = (bool) autoDetect;
        }

        if (IsRecording)
        {
            // if sample rate or frame size have changed, restart recording
            if (sampleRate != SampleRate || frameSize != FrameLength)
            {
                RestartRecording += () =>
                {
                    StartRecording(SampleRate, FrameLength, autoDetect);
                    RestartRecording = null;
                };
                StopRecording();
            }

            return;
        }

        SampleRate = sampleRate;
        FrameLength = frameSize;

        // Use the device's native sample rate to avoid Unity's silent mis-labelling bug.
        // If the device only supports e.g. 24000 Hz but we ask for 16000 Hz, Unity records
        // at 24000 Hz while labelling the clip as 16000 Hz, corrupting all position math.
        Microphone.GetDeviceCaps(CurrentDeviceName, out int minFreq, out int maxFreq);
        _deviceSampleRate = (maxFreq == 0) ? sampleRate : maxFreq;

        // 2-second clip to accommodate higher-than-16kHz device rates
        _audioClip = Microphone.Start(CurrentDeviceName, true, 2, _deviceSampleRate);

        Debug.Log($"[VoiceProcessor] Device: '{CurrentDeviceName}' | Output: {sampleRate} Hz | Recording at: {_deviceSampleRate} Hz");

        _recordDataCoroutine = StartCoroutine(RecordData());
    }

    /// <summary>
    /// Stops recording audio
    /// </summary>
    public void StopRecording()
    {
        if (!IsRecording)
            return;

        Microphone.End(CurrentDeviceName);
        Destroy(_audioClip);
        _audioClip = null;
        _didDetect = false;

        if (_recordDataCoroutine != null)
        {
            StopCoroutine(_recordDataCoroutine);
            _recordDataCoroutine = null;
        }
    }

    /// <summary>
    /// Loop for buffering incoming audio data and delivering frames
    /// </summary>
    IEnumerator RecordData()
    {
        // How many device-rate samples correspond to one output frame at SampleRate.
        // e.g. device=24000, output=16000, FrameLength=512 → inputFrameLength=768
        int inputFrameLength = Mathf.CeilToInt((float)FrameLength * _deviceSampleRate / SampleRate);
        float[] inputBuffer = new float[inputFrameLength];
        float[] sampleBuffer = new float[FrameLength]; // resampled output
        int startReadPos = 0;

        if (OnRecordingStart != null)
            OnRecordingStart.Invoke();

        while (IsRecording)
        {
            int curClipPos = Microphone.GetPosition(CurrentDeviceName);
            if (curClipPos < startReadPos)
                curClipPos += _audioClip.samples;

            int samplesAvailable = curClipPos - startReadPos;
            if (samplesAvailable < inputFrameLength)
            {
                yield return null;
                continue;
            }

            int endReadPos = startReadPos + inputFrameLength;
            if (endReadPos > _audioClip.samples)
            {
                // fragmented read (wraps around to beginning of clip)
                int numSamplesClipEnd = _audioClip.samples - startReadPos;
                float[] endClipSamples = new float[numSamplesClipEnd];
                _audioClip.GetData(endClipSamples, startReadPos);

                int numSamplesClipStart = endReadPos - _audioClip.samples;
                float[] startClipSamples = new float[numSamplesClipStart];
                _audioClip.GetData(startClipSamples, 0);

                Buffer.BlockCopy(endClipSamples, 0, inputBuffer, 0, numSamplesClipEnd * sizeof(float));
                Buffer.BlockCopy(startClipSamples, 0, inputBuffer, numSamplesClipEnd * sizeof(float), numSamplesClipStart * sizeof(float));
            }
            else
            {
                _audioClip.GetData(inputBuffer, startReadPos);
            }

            startReadPos = endReadPos % _audioClip.samples;

            // Resample from _deviceSampleRate to SampleRate via linear interpolation
            if (_deviceSampleRate == SampleRate)
            {
                Buffer.BlockCopy(inputBuffer, 0, sampleBuffer, 0, FrameLength * sizeof(float));
            }
            else
            {
                for (int i = 0; i < FrameLength; i++)
                {
                    float srcPos = (float)i * (inputFrameLength - 1) / (FrameLength - 1);
                    int srcIndex = (int)srcPos;
                    float t = srcPos - srcIndex;
                    int nextIndex = Mathf.Min(srcIndex + 1, inputFrameLength - 1);
                    sampleBuffer[i] = inputBuffer[srcIndex] * (1f - t) + inputBuffer[nextIndex] * t;
                }
            }
            if (_autoDetect == false)
            {
                _transmit =_audioDetected = true;
            }
            else
            {
                float maxVolume = 0.0f;

                for (int i = 0; i < sampleBuffer.Length; i++)
                {
                    if (sampleBuffer[i] > maxVolume)
                    {
                        maxVolume = sampleBuffer[i];
                    }
                }

                if (maxVolume >= _minimumSpeakingSampleValue)
                {
                    _transmit= _audioDetected = true;
                    _timeAtSilenceBegan = Time.time;
                }
                else
                {
                    _transmit = false;

                    if (_audioDetected && Time.time - _timeAtSilenceBegan > _silenceTimer)
                    {
                        _audioDetected = false;
                    }
                }
            }

            if (_audioDetected)
            {
                _didDetect = true;
                // converts to 16-bit int samples
                short[] pcmBuffer = new short[sampleBuffer.Length];
                for (int i = 0; i < FrameLength; i++)
                {
                    pcmBuffer[i] = (short) Math.Floor(sampleBuffer[i] * short.MaxValue);
                }

                // raise buffer event
                if (OnFrameCaptured != null && _transmit)
                    OnFrameCaptured.Invoke(pcmBuffer);
            }
            else
            {
                if (_didDetect)
                {
                    if (OnRecordingStop != null)
                        OnRecordingStop.Invoke();
                    _didDetect = false;
                }
            }
        }


        if (OnRecordingStop != null)
            OnRecordingStop.Invoke();
        if (RestartRecording != null)
            RestartRecording.Invoke();
    }
}
