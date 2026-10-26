using System.Collections.Generic;

namespace NoiseReduction.Desktop
{
    // переводы лежат по одному файлу на язык, ключ везде русский исходник
    internal static partial class Translations
    {
        internal static Dictionary<string, Dictionary<string, string>> All(Dictionary<string, string> english)
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                { "en", english }, { "zh", Zh }, { "es", Es }, { "hi", Hi }, { "ar", Ar }, { "pt", Pt },
                { "fr", Fr }, { "de", De }, { "ja", Ja }, { "ko", Ko }, { "it", It }, { "tr", Tr },
                { "pl", Pl }, { "uk", Uk }, { "id", Id }, { "vi", Vi }, { "nl", Nl }, { "th", Th }, { "fa", Fa }
            };
        }
    }
}
