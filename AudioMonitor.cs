using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

public class AudioMonitor
{
    private WasapiLoopbackCapture waveIn;
    private MemoryStream audioStream;
    private bool isRecording = false;
    private DateTime lastAudioDetectedTime;
    private const int SilenceDuration = 3000; // 3 seconds
    private const int InitialListenDuration = 1000; // 1 second

    private readonly object lockObject = new object();


    public void MonitorAndRecordAudioAsync()
    {
        StopRecording();

        waveIn = new WasapiLoopbackCapture();
        audioStream = new MemoryStream();
        waveIn.DataAvailable += OnDataAvailable;
        Console.WriteLine("Listening for audio...");
        waveIn.RecordingStopped += OnRecordingStopped;
        lastAudioDetectedTime = DateTime.Now;
        waveIn.StartRecording();       
            
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (lockObject)
        {
            if (e.BytesRecorded > 0)
            {
                float volume = CalculateRMS(e.Buffer, e.BytesRecorded);

                // Threshold for audio detection
                if (volume > 0.1) // Example threshold
                {
                    isRecording = true;
                    lastAudioDetectedTime = DateTime.Now;
                    if (isRecording)
                    {
                        audioStream.Write(e.Buffer, 0, e.BytesRecorded);
                    }
                }
                else
                {
                    if ((DateTime.Now - lastAudioDetectedTime).TotalMilliseconds > (isRecording ? SilenceDuration: InitialListenDuration))
                    {
                        StopRecording();
                    }
                }
            }
        }
    }

    private static float CalculateRMS(byte[] buffer, int bytesRecorded)
    {
        int bytesPerSample = sizeof(float); // 由于 NAudio 使用 IEEE float
        int sampleCount = bytesRecorded / bytesPerSample;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.ToSingle(buffer, i * bytesPerSample);
        }

        // 计算 RMS（均方根）
        float sumOfSquares = samples.Select(sample => sample * sample).Sum();
        return (float)Math.Sqrt(sumOfSquares / sampleCount);
    }

    private void StopRecording()
    {
        if(waveIn == null)
        {
            return;
        }
        Console.WriteLine("Stopping recording...");
        waveIn.StopRecording();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (isRecording)
        {
            isRecording = false;
            SaveAudioToFile();
        }
        else
        {
            Console.WriteLine("No Audio detected.");
        }  
    }

    private void SaveAudioToFile()
    {
        lock(lockObject)
        {
            if(audioStream.Length == 0)
            {
                Console.WriteLine("No audio captured, so no file saved");
                return;
            }
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "recorded_audio.wav");
            using (WaveFileWriter writer = new WaveFileWriter(filePath, waveIn.WaveFormat))
            {
                audioStream.Position = 0;
                audioStream.CopyTo(writer);
            }
            Console.WriteLine($"Audio saved to {filePath}");
        }       
    }
}