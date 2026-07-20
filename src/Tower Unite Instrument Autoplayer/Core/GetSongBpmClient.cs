using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tower_Unite_Instrument_Autoplayer.Core
{
    /// <summary>
    /// Looks up a song's real, measured tempo from getsongbpm.com's database - a genuinely
    /// different kind of signal from anything else this program calibrates from: it's the
    /// actual BPM of the real recording (from audio analysis), not a community member's "how
    /// long it took me to play this" estimate, and not derived from any assumption about how
    /// many notation characters make up a beat. Requires the user's own free API key (obtained
    /// by registering at https://getsongbpm.com/api), stored in Properties.Settings - this
    /// can't work with no configuration at all, since the API requires every request to be
    /// authenticated and getsongbpm's terms don't allow embedding one shared key in a
    /// distributed application.
    /// </summary>
    public static class GetSongBpmClient
    {
        private static readonly HttpClient httpClient = new HttpClient();

        /// <summary>
        /// Looks up title/artist and returns the first matching song's tempo, or null if no API
        /// key is configured, nothing matched, or the request failed for any reason - this is
        /// always just one more signal among several, so a failure here should fall through to
        /// the next one rather than stopping the import.
        /// </summary>
        public static async Task<int?> TryGetBpmAsync(string apiKey, string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(title))
                return null;

            try
            {
                string lookup = string.IsNullOrWhiteSpace(artist)
                    ? "song:" + Uri.EscapeDataString(title)
                    : "song:" + Uri.EscapeDataString(title) + " artist:" + Uri.EscapeDataString(artist);

                string url = "https://api.getsong.co/search/?api_key=" + Uri.EscapeDataString(apiKey)
                    + "&type=both&lookup=" + lookup + "&limit=1";

                string json = await httpClient.GetStringAsync(url).ConfigureAwait(false);

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);

                object searchObj;
                if (root == null || !root.TryGetValue("search", out searchObj))
                    return null;

                ArrayList_Compat results = ArrayList_Compat.From(searchObj);
                if (results == null || results.Count == 0)
                    return null;

                Dictionary<string, object> firstResult = results[0] as Dictionary<string, object>;
                if (firstResult == null)
                    return null;

                object tempoObj;
                if (!firstResult.TryGetValue("tempo", out tempoObj) || tempoObj == null)
                    return null;

                //The documented type is "Integrer" but the API's own example response shows it
                //as a quoted string ("tempo":"220") - handle either shape rather than assuming.
                int tempo;
                if (tempoObj is int)
                {
                    tempo = (int)tempoObj;
                }
                else if (!int.TryParse(tempoObj.ToString(), out tempo))
                {
                    return null;
                }

                if (tempo < 20 || tempo > 400)
                    return null; //Sanity bound against a malformed/unexpected response.

                return tempo;
            }
            catch
            {
                //Any failure here (network, bad key, no match, unexpected response shape) just
                //means this signal isn't available for this song - calibration falls through to
                //the next one rather than failing the whole import over an optional lookup.
                return null;
            }
        }

        /// <summary>
        /// JavaScriptSerializer deserialises a JSON array as System.Collections.ArrayList - this
        /// is a tiny, explicit wrapper around that so the calling code above doesn't need a
        /// direct reference to the non-generic collections namespace just for one array access.
        /// </summary>
        private class ArrayList_Compat
        {
            private readonly System.Collections.ArrayList inner;
            private ArrayList_Compat(System.Collections.ArrayList list) { inner = list; }

            public static ArrayList_Compat From(object value)
            {
                System.Collections.ArrayList list = value as System.Collections.ArrayList;
                return list == null ? null : new ArrayList_Compat(list);
            }

            public int Count => inner.Count;
            public object this[int index] => inner[index];
        }
    }
}
