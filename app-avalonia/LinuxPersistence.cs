using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using NoiseReduction.Desktop;

namespace NoiseReduction.Avalonia
{
    internal sealed class LinuxPersistence
    {
        private readonly string directory;
        private string settingsError, preferencesError;
        private string recoveryWarning;
        private bool settingsBlocked, preferencesBlocked;
        internal string Error => settingsError ?? preferencesError;
        internal string RecoveryWarning => recoveryWarning;

        internal LinuxPersistence(string directory) => this.directory = directory;
        internal Settings LoadSettings()
        {
            string path = Path.Combine(directory, "settings.json");
            if (!File.Exists(path)) return new();
            try
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                    if (settings?.Profiles == null) throw new SerializationException("Microphone profiles are missing");
                }
                catch (SerializationException error) { PreserveInvalid(path, error); return new(); }
                return Settings.Load(path);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                settingsBlocked = true;
                settingsError = "Could not safely load microphone settings: " + error.Message;
                return new();
            }
        }

        internal LinuxPreferences LoadPreferences()
        {
            string path = Path.Combine(directory, "linux.json");
            try
            {
                try { return LinuxPreferences.Load(path); }
                catch (Exception error) when (error is System.Text.Json.JsonException || error is InvalidDataException)
                {
                    PreserveInvalid(path, error);
                    return new();
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                preferencesBlocked = true;
                preferencesError = "Could not safely load Linux audio settings: " + error.Message;
                return new();
            }
        }

        private void PreserveInvalid(string path, Exception error)
        {
            string backup = path + ".invalid-" + Guid.NewGuid().ToString("N");
            File.Move(path, backup);
            recoveryWarning = (recoveryWarning == null ? "" : recoveryWarning + "\n") +
                "Invalid settings preserved at " + backup + ": " + error.Message;
        }
        internal void SaveSettings(Settings settings)
        {
            try
            {
                if (settingsBlocked) throw new IOException("The original microphone settings could not be preserved; correct file access and restart");
                settings.Save(Path.Combine(directory, "settings.json"));
                settingsError = null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                settingsError = "Could not save microphone settings: " + error.Message;
                throw;
            }
        }

        internal void SavePreferences(LinuxPreferences preferences)
        {
            try
            {
                if (preferencesBlocked) throw new IOException("The original Linux audio settings could not be preserved; correct file access and restart");
                preferences.Save(Path.Combine(directory, "linux.json"));
                preferencesError = null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                preferencesError = "Could not save Linux audio ownership and rules: " + error.Message;
                throw;
            }
        }
    }
}
