using NoiseReduction.Avalonia;

namespace NoiseReduction.Linux.Tests
{
    internal static class VoiceTests
    {
        [Test]
        private static void VoicePlaybackSwitchesWithoutMovingPosition()
        {
            var cursor = new VoicePlayback();
            float[] original = { 1, 2, 3, 4 }, processed = { -1, -2, -3, -4 };
            float[] block = new float[3];
            cursor.Fill(original, processed, true, block);
            Assert.True(block.SequenceEqual(new float[] { 1, 2, 3 }));
            cursor.Fill(original, processed, false, block);
            Assert.True(block.SequenceEqual(new float[] { -4, -1, -2 }));
            Assert.Equal(3L + 3L, cursor.FramesWritten);
        }

        [Test]
        private static void VoicePcmPreservesPartialSampleAndBounds()
        {
            byte[] bytes = new byte[16];
            Buffer.BlockCopy(new float[] { .25f, -.5f, .75f, 1 }, 0, bytes, 0, 16);
            var pcm = new VoicePcm(3);
            pcm.Append(bytes.AsSpan(0, 3));
            pcm.Append(bytes.AsSpan(3, 8));
            pcm.Append(bytes.AsSpan(11));
            Assert.True(pcm.ToArray().SequenceEqual(new float[] { .25f, -.5f, .75f }));
            Assert.True(pcm.Full);
        }

        [Test]
        private static async Task VoiceInvalidInputIsReportedAndClearDiscardsIt()
        {
            var queued = new List<Action>();
            using var voice = new VoiceTest(action => queued.Add(action));
            await voice.StartAsync(null, 1);
            Assert.True(voice.Error.Length > 0, "Missing input must be reported");
            voice.Clear();
            foreach (var action in queued.ToArray()) action();
            Assert.True(!voice.Recording && !voice.Playing && !voice.HasRecording);
            Assert.Equal(string.Empty, voice.Error);
        }

        [Test]
        private static void VoiceReprocessedPlaybackKeepsItsSamplePosition()
        {
            var cursor = new VoicePlayback(3);
            float[] original = { 1, 2, 3, 4 };
            float[] block = new float[2];
            cursor.Fill(original, Array.Empty<float>(), true, block);
            Assert.True(block.SequenceEqual(new float[] { 4, 1 }));
            cursor.Fill(original, new float[] { 10, 20, 30, 40 }, false, block);
            Assert.True(block.SequenceEqual(new float[] { 20, 30 }));
        }

        [Test]
        private static void VoiceLiveWaveformDropsPeaksOlderThanFourSeconds()
        {
            var pcm = new VoicePcm(960 * 201);
            byte[] bucket = new byte[960 * 4];
            Buffer.BlockCopy(Enumerable.Repeat(1f, 960).ToArray(), 0, bucket, 0, bucket.Length);
            pcm.Append(bucket);
            Array.Clear(bucket);
            for (int i = 0; i < 200; i++) pcm.Append(bucket);
            Assert.Equal(200, pcm.LivePeaks().Length);
            Assert.True(pcm.LivePeaks().All(peak => peak == 0));
        }

        [Test]
        private static async Task VoiceClearDuringCaptureCannotRestoreOldPcm()
        {
            await WithCaptureCommand("printf '\\000\\000\\200\\077'\nexec sleep 30\n", async () =>
            {
                using var voice = new VoiceTest();
                await voice.StartAsync(new Microphone("physical", "Fixture", "source", true), 0).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(voice.Recording);
                Assert.Equal(1, voice.LivePeaks.Length);
                voice.Clear();
                await voice.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(!voice.Recording && !voice.Playing && !voice.Processing && !voice.HasRecording);
                Assert.Equal(0, voice.Original.Length);
                Assert.Equal(string.Empty, voice.Error);
            });
        }

        [Test]
        private static async Task VoiceDisposeWhileWaitingForAudioCancelsStart()
        {
            await WithCaptureCommand("exec sleep 30\n", async () =>
            {
                var voice = new VoiceTest();
                Task start = voice.StartAsync(new Microphone("physical", "Fixture", "source", true), 0);
                voice.Dispose();
                await start.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(!voice.Recording && !voice.HasRecording);
            });
        }

        [Test]
        private static async Task VoiceClearCancelsPendingReprocessing()
        {
            await WithCaptureCommand("printf '\\000\\000\\200\\077'\nexec sleep 30\n", async () =>
            {
                using var voice = new VoiceTest();
                await voice.StartAsync(new Microphone("physical", "Fixture", "source", true), 1).WaitAsync(TimeSpan.FromSeconds(3));
                var processing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                voice.Changed += () => { if (voice.Processing) processing.TrySetResult(true); };
                Task stopping = voice.StopRecordingAsync();
                await processing.Task.WaitAsync(TimeSpan.FromSeconds(3));
                voice.Clear();
                await stopping.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(!voice.Processing && !voice.HasRecording);
                Assert.Equal(0, voice.Processed.Length);
                Assert.Equal(string.Empty, voice.Error);
            });
        }

        private static async Task WithCaptureCommand(string script, Func<Task> run)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Voice capture fixtures require Linux");
            string directory = Path.Combine(Path.GetTempPath(), "noise-reduction-voice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string command = Path.Combine(directory, "parec");
            string previous = Environment.GetEnvironmentVariable("PATH");
            try
            {
                await File.WriteAllTextAsync(command, "#!/bin/sh\n" + script, new System.Text.UTF8Encoding(false));
                File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + previous);
                await run();
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", previous);
                Directory.Delete(directory, true);
            }
        }
    }
}
