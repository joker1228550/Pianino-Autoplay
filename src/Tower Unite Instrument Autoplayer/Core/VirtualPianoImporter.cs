using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tower_Unite_Instrument_Autoplayer.Core
{
    /// <summary>
    /// A single search hit from virtualpiano.net.
    /// </summary>
    public class VirtualPianoSearchResult
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Url { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Artist) ? Title : $"{Title} — {Artist}";
        }
    }

    /// <summary>
    /// One of virtualpiano.net's music sheet genres/categories, as shown in the Song Selection
    /// tab's genre list. UrlPath is the path segment after the domain (already including whether
    /// it's a "category" or a "tag", since those use different URL prefixes on the site).
    /// </summary>
    public class VirtualPianoGenre
    {
        public string DisplayName { get; set; }
        public string UrlPath { get; set; }

        public override string ToString()
        {
            return DisplayName;
        }
    }

    /// <summary>
    /// A single song listed on one of virtualpiano.net's genre/category pages.
    /// </summary>
    public class VirtualPianoCategorySong
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Url { get; set; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Artist) ? Title : $"{Title} — {Artist}";
        }
    }

    /// <summary>
    /// Statistics produced while converting a Virtual Piano sheet, used both to inform the user
    /// and to calibrate playback delay (see VirtualPianoImporter.TryGetSuggestedDelays).
    /// </summary>
    public class ConversionStats
    {
        public int ChordCount { get; set; }
        public int FastGroupCount { get; set; }
        public int PauseCount { get; set; }
        public int SkippedCharacterCount { get; set; }

        //Number of individual Play()-then-sleep note-events that will occur at the Settings
        //tab's normal-speed delay (single notes and chords - a chord is one simultaneous event
        //no matter how many notes it contains).
        public int NormalSpeedEventCount { get; set; }

        //Same, but for note-events inside a "{...}" fast-speed block (each character in a fast
        //run/sequence plays as its own event, at the fast-speed delay instead of the normal one).
        public int FastSpeedEventCount { get; set; }

        //Total pause time expressed as a multiple of the normal-speed delay (e.g. one short and
        //one long pause = ShortPauseMultiplier + LongPauseMultiplier). Letting this be fractional
        //keeps it directly comparable to NormalSpeedEventCount/FastSpeedEventCount when solving
        //for a per-note delay from a total recommended playback time.
        public double PauseWeight { get; set; }
    }

    /// <summary>
    /// The result of downloading and converting a single virtualpiano.net music sheet.
    /// </summary>
    public class VirtualPianoImportResult
    {
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Difficulty { get; set; }
        public string SourceUrl { get; set; }
        public string RawNotation { get; set; }
        public string ConvertedNotation { get; set; }
        public int ChordCount { get; set; }
        public int FastGroupCount { get; set; }
        public int PauseCount { get; set; }
        public int SkippedCharacterCount { get; set; }

        //Filled in by ImportFromUrlAsync after it calibrates and registers this song's pause
        //delays (see RegisterPauseDelays), so the GUI can show what was applied without having
        //to redo the same calculation itself.
        public bool SpeedSuggestionApplied { get; set; }
        public int SuggestedNormalDelayMs { get; set; }
        public int SuggestedFastDelayMs { get; set; }
        public int SuggestedBpm { get; set; }
        public string SuggestedDelaySource { get; set; }
    }

    /// <summary>
    /// Searches virtualpiano.net, downloads a chosen music sheet and converts its notation
    /// into notation this Autoplayer understands.
    ///
    /// This is possible because Tower Unite's in-game piano and Virtual Piano use the exact
    /// same underlying 36-key layout: the letters A-Z and digits 0-9, where holding Shift
    /// plays the "black key"/high version of whichever key is pressed. The only real
    /// differences between the two notations are:
    ///
    ///   1) Virtual Piano writes the black key of a digit using the symbol a US keyboard
    ///      produces for Shift+that digit (! @ # $ % ^ &amp; * ( )), while this Autoplayer
    ///      was written around a Danish/Nordic keyboard, which produces different symbols
    ///      for the very same physical keys (! " # ¤ % &amp; / ( ) =). See DigitSymbolMap below.
    ///   2) Virtual Piano's notation has a richer set of timing rules (chords that mix high
    ///      and low keys, fast unbracketed runs, and several levels of pause using spaces and
    ///      '|') than this simpler Autoplayer engine supports. ConvertNotation approximates
    ///      these as closely as this program's playback engine allows - see the comments
    ///      in ConvertNotation for exactly how each case is handled.
    /// </summary>
    public static class VirtualPianoImporter
    {
        private const string BaseUrl = "https://virtualpiano.net";

        //A single, reused HttpClient (creating one per request can exhaust sockets under load).
        private static readonly HttpClient httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            try
            {
                //Some older Windows setups default to an older TLS version than the one
                //virtualpiano.net requires - make sure TLS 1.2 is offered.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch (NotSupportedException)
            {
                //Enum value not supported on this .NET/OS combination - HTTPS may still work
                //via whatever the OS default is, so we don't treat this as fatal.
            }

            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TowerUniteInstrumentAutoplayer/2.3");
            return client;
        }

        #region Digit "black key" symbol conversion

        //Virtual Piano writes the black key of a digit using the symbol a standard US keyboard
        //produces for Shift + that digit. This Autoplayer instead uses the symbol a Danish/Nordic
        //keyboard produces for Shift + the very same physical digit key. Both sides represent
        //exactly the same 10 keys, just typed on a different keyboard layout, so converting
        //between them is a straight one-to-one swap.
        private static readonly Dictionary<char, char> DigitSymbolMap = new Dictionary<char, char>
        {
            ['!'] = '!', // Shift+1
            ['@'] = '"', // Shift+2
            ['#'] = '#', // Shift+3
            ['$'] = '¤', // Shift+4
            ['%'] = '%', // Shift+5
            ['^'] = '&', // Shift+6
            ['&'] = '/', // Shift+7
            ['*'] = '(', // Shift+8
            ['('] = ')', // Shift+9
            [')'] = '=', // Shift+0
        };

        private static readonly HashSet<char> ValidPassThroughChars = new HashSet<char>(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");

        #endregion

        #region Pause-character registration

        //Virtual Piano expresses short/medium/long pauses using the '|' character combined with
        //surrounding spaces, and an extended pause using a paragraph break. This Autoplayer has
        //no built-in concept of a variable-length pause, but it does support arbitrary custom
        //"delay" characters (see the Settings tab). We reuse three characters that are not part
        //of the normal note alphabet to stand in for these. Because these are registered the
        //same way any other custom delay is, the user can freely retune or remove them
        //afterwards from the Settings tab.
        public const char ShortPauseChar = ';';
        public const char LongPauseChar = ':';
        public const char ExtendedPauseChar = ',';

        //Used only by the pause-weight tracking in ConvertNotation below (kept for the
        //ChordCount/FastGroupCount/PauseCount reporting shown after an import) - no longer by
        //delay registration itself, which now uses fixed values regardless of any song's tempo.
        private const double ShortPauseMultiplier = 0.3;
        private const double LongPauseMultiplier = 1.2;
        private const double ExtendedPauseMultiplier = 2.2;

        /// <summary>
        /// Registers the three pause characters with fixed default delay times the first time
        /// they're needed. Because these are registered the same way any other custom delay is,
        /// the user can freely retune or remove them afterwards from the Customize tab.
        /// </summary>
        private static void RegisterPauseDelays()
        {
            EnsureDelay(ShortPauseChar, 150);
            EnsureDelay(LongPauseChar, 350);
            EnsureDelay(ExtendedPauseChar, 600);
        }

        private static void EnsureDelay(char character, int time)
        {
            if (!Autoplayer.CheckDelayExists(character))
            {
                try
                {
                    Autoplayer.AddDelay(character, time);
                }
                catch (AutoplayerCustomDelayException)
                {
                    //Someone else already registered it a moment ago - fine, ignore.
                }
            }
        }

        #endregion

        #region Notation conversion

        //Recognises, in priority order: a run of '|' (optionally with spaces mixed in), a
        //bracketed group, a run of ordinary note characters with no internal whitespace, or a
        //plain run of whitespace. Because "pause" is listed first, a '|' together with its
        //surrounding spaces is always consumed as a single unit rather than falling through to
        //the plain "space" case.
        private static readonly Regex TokenRegex = new Regex(
            @"(?<pause>(?:[ \t]*\|[ \t]*)+)|(?<bracket>\[[^\]]*\])|(?<run>[^\s\[\]|]+)|(?<space>[ \t]+)",
            RegexOptions.Compiled);

        /// <summary>
        /// Converts raw Virtual Piano notation into notation this Autoplayer can load. This
        /// never throws for malformed or unexpected input - unrecognised characters are simply
        /// dropped, exactly like Autoplayer.AddNoteFromChar already does for unknown characters
        /// elsewhere in this program.
        /// </summary>
        public static string ConvertNotation(string vpText, out ConversionStats stats)
        {
            stats = new ConversionStats();

            if (string.IsNullOrEmpty(vpText))
                return string.Empty;

            //Note: the short/long/extended pause characters emitted below are registered as
            //delays by the caller (ImportFromUrlAsync), once this song's own calibrated delay is
            //known - see RegisterPauseDelays. Converting text and deciding delay times are
            //deliberately kept separate so the same conversion result can be recalibrated
            //without re-parsing the source notation.

            //Normalise line endings so a blank line (Virtual Piano's "paragraph break", i.e.
            //an extended pause) can be detected the same way regardless of source formatting.
            string[] lines = vpText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            StringBuilder output = new StringBuilder();
            bool previousLineWasBlank = false;
            bool anyContentYet = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim(' ', '\t');

                if (line.Length == 0)
                {
                    //A blank line = a paragraph break = Virtual Piano's "extended pause".
                    //Only emit one, and only once we've already produced something.
                    if (anyContentYet && !previousLineWasBlank)
                    {
                        output.Append(ExtendedPauseChar);
                        stats.PauseCount++;
                        stats.PauseWeight += ExtendedPauseMultiplier;
                        output.Append('\n');
                    }
                    previousLineWasBlank = true;
                    continue;
                }
                previousLineWasBlank = false;

                foreach (Match match in TokenRegex.Matches(line))
                {
                    if (match.Groups["pause"].Success)
                    {
                        string p = match.Value;
                        int pipes = 0;
                        int spaces = 0;
                        foreach (char c in p)
                        {
                            if (c == '|') pipes++;
                            else spaces++;
                        }

                        //Virtual Piano distinguishes a bare "|" (shortest), one extra space
                        //("long"), and two extra spaces or two pipes (both "longer"/"longest",
                        //merged here into a single "extended" tier).
                        char pauseChar;
                        double pauseWeight;
                        if (pipes >= 2 || spaces >= 2)
                        {
                            pauseChar = ExtendedPauseChar;
                            pauseWeight = ExtendedPauseMultiplier;
                        }
                        else if (spaces == 1)
                        {
                            pauseChar = LongPauseChar;
                            pauseWeight = LongPauseMultiplier;
                        }
                        else
                        {
                            pauseChar = ShortPauseChar;
                            pauseWeight = ShortPauseMultiplier;
                        }
                        output.Append(pauseChar);
                        stats.PauseCount++;
                        stats.PauseWeight += pauseWeight;
                        anyContentYet = true;
                    }
                    else if (match.Groups["bracket"].Success)
                    {
                        string inner = match.Value.Substring(1, match.Value.Length - 2);
                        bool isFastSequence = inner.Any(char.IsWhiteSpace);

                        int skippedHere = 0;
                        string cleaned = CleanAndMapChars(inner.Where(c => !char.IsWhiteSpace(c)), ref skippedHere);
                        stats.SkippedCharacterCount += skippedHere;

                        if (cleaned.Length == 0)
                        {
                            //Nothing usable was left in this group - drop it entirely rather
                            //than emit empty brackets.
                            continue;
                        }

                        if (isFastSequence)
                        {
                            //Virtual Piano: "[a s d f]" = play the sequence at fastest possible
                            //speed. This Autoplayer's closest equivalent is its fast-speed toggle.
                            if (cleaned.Length >= 2)
                            {
                                output.Append('{').Append(cleaned).Append('}');
                                stats.FastGroupCount++;
                                //Each character here plays as its own event at fast speed.
                                stats.FastSpeedEventCount += cleaned.Length;
                            }
                            else
                            {
                                output.Append(cleaned);
                                //Too short to wrap in a fast toggle - plays as one normal note.
                                stats.NormalSpeedEventCount += cleaned.Length;
                            }
                        }
                        else
                        {
                            //Virtual Piano: "[asdf]" = play notes together simultaneously - a
                            //direct match for this Autoplayer's own multi-note chord syntax.
                            output.Append('[').Append(cleaned).Append(']');
                            stats.ChordCount++;
                            //A chord is a single simultaneous timing event at normal speed,
                            //however many notes it contains.
                            stats.NormalSpeedEventCount++;
                        }
                        anyContentYet = true;
                    }
                    else if (match.Groups["run"].Success)
                    {
                        int skippedHere = 0;
                        string cleaned = CleanAndMapChars(match.Value, ref skippedHere);
                        stats.SkippedCharacterCount += skippedHere;

                        if (cleaned.Length == 0)
                            continue;

                        //Real "quickly one after the other" runs on Virtual Piano are usually
                        //short, but chunk rather than cap outright: splitting a long run into
                        //several back-to-back fast bursts keeps the whole passage feeling
                        //consistently quick, instead of an abrupt cliff back to normal speed
                        //partway through (which reads as an odd stall mid-phrase) the moment a
                        //run happens to be longer than some fixed threshold.
                        const int fastChunkSize = 6;
                        if (cleaned.Length >= 2)
                        {
                            for (int i = 0; i < cleaned.Length; i += fastChunkSize)
                            {
                                int chunkLen = Math.Min(fastChunkSize, cleaned.Length - i);
                                string chunk = cleaned.Substring(i, chunkLen);
                                if (chunkLen >= 2)
                                {
                                    //Virtual Piano: "asdf" (no brackets, no spaces) = play notes
                                    //one after the other quickly. Approximate with the fast toggle.
                                    output.Append('{').Append(chunk).Append('}');
                                    stats.FastGroupCount++;
                                    stats.FastSpeedEventCount += chunkLen;
                                }
                                else
                                {
                                    output.Append(chunk);
                                    stats.NormalSpeedEventCount += chunkLen;
                                }
                            }
                        }
                        else
                        {
                            output.Append(cleaned);
                            stats.NormalSpeedEventCount += cleaned.Length;
                        }
                        anyContentYet = true;
                    }
                    //A plain "space" match is Virtual Piano's baseline "play each note after a
                    //short pause" - this Autoplayer already inserts its own default delay after
                    //every single note, so nothing extra needs to be emitted for it. We also
                    //deliberately never emit a literal space character here, since a space is
                    //itself a playable note (the spacebar) in this Autoplayer's dictionary.
                }

                output.Append('\n');
            }

            return output.ToString();
        }

        private static string CleanAndMapChars(IEnumerable<char> chars, ref int skippedCount)
        {
            StringBuilder sb = new StringBuilder();
            int localSkipped = 0;
            foreach (char c in chars)
            {
                char mapped;
                if (DigitSymbolMap.TryGetValue(c, out mapped))
                {
                    sb.Append(mapped);
                }
                else if (ValidPassThroughChars.Contains(c))
                {
                    sb.Append(c);
                }
                else
                {
                    localSkipped++;
                }
            }
            skippedCount += localSkipped;
            return sb.ToString();
        }

        #endregion

        #region Suggested playback speed

        /// <summary>
        /// Works out a starting point for the Settings tab's normal/fast delays for a just-
        /// imported song, based on Virtual Piano's stated difficulty tier for it (Super Easy /
        /// Easy / Intermediate / Expert) alone - deliberately not calibrated against any
        /// per-song timing data pulled from the site (recommended completion time, explicit
        /// tempo, etc.), since that approach turned out to be unreliable in practice. The tier
        /// BPM values are converted to milliseconds with a simple, fixed formula (a normal note
        /// = one beat at that BPM, a fast note = half of one), matching Virtual Piano's own
        /// metronome model (documented range: 40-218 BPM) rather than an arbitrary millisecond
        /// number. effectiveBpm is reported back alongside the millisecond values since it's a
        /// more meaningful, transferable number than milliseconds when comparing tempo across
        /// different songs. This is only ever a starting point - the user can retune the result
        /// freely afterwards like any other delay.
        /// </summary>
        /// <summary>
        /// Works out a starting point for the Settings tab's normal/fast delays for a just-
        /// imported song, fully automatically - no difficulty selection or manual correction
        /// should be needed for this to land close to correct.
        ///
        /// Highest priority: Virtual Piano's own "recommended time to play this music sheet",
        /// where present. This is solved backwards from a real, community-verified duration
        /// (recommendedTimeMs = normalDelayMs * weighted note/chord/pause units), which sidesteps
        /// a problem every other approach tried here shared: they all convert a BPM figure
        /// (whether guessed per difficulty tier, or read directly off the page) into a delay by
        /// assuming one notation character equals one beat. If that assumption is off - and
        /// real VP notation typically packs more than one character into a beat during faster
        /// passages - every BPM-based estimate ends up consistently too fast, regardless of how
        /// accurate the BPM figure itself is. Solving from a known total duration has no such
        /// assumption baked in: whatever the real characters-per-beat ratio for this specific
        /// song is, the maths still reproduces the actual, correct duration.
        ///
        /// Fallback: the difficulty tier alone, for the minority of pages with no recommended
        /// time - necessarily a rougher estimate, since it's the same kind of BPM-based guess
        /// described above, just with no per-song duration to correct it against.
        /// </summary>
        public static bool TryGetSuggestedDelays(string difficulty, int? recommendedTimeMs, ConversionStats stats,
            out int normalDelayMs, out int fastDelayMs, out int effectiveBpm, out string source)
        {
            //Assigned unconditionally up front so every path out of this method - including the
            //compiler's view of it - definitely has a value, rather than relying on it working
            //out that bpmForTier > 0 (checked separately, below) implies source was also set
            //back in whichever if/else-if branch matched. The C# compiler's definite-assignment
            //check doesn't reason about values correlating like that across two variables; this
            //was actually flagged as error CS0177 when compiled for real.
            normalDelayMs = 0;
            fastDelayMs = 0;
            effectiveBpm = 0;
            source = null;

            if (recommendedTimeMs.HasValue && recommendedTimeMs.Value > 0 && stats != null)
            {
                //Solve recommendedTimeMs = normalDelayMs * (normalEvents + fastEvents/2 + pauseWeight)
                //for normalDelayMs, since a fast-speed event takes about half of a normal-speed
                //one, and each pause adds pauseWeight-many normal-delay-equivalents on top. This
                //doesn't assume any particular characters-per-beat ratio - it works out whatever
                //per-character delay reproduces this specific song's own real, known duration.
                double weightedUnits = stats.NormalSpeedEventCount + stats.FastSpeedEventCount / 2.0 + stats.PauseWeight;
                if (weightedUnits > 0)
                {
                    int perNoteMs = (int)Math.Round(recommendedTimeMs.Value / weightedUnits);
                    //Clamp to Virtual Piano's own documented BPM range (40-218, i.e. roughly
                    //275-1500ms per beat) rather than an arbitrary window - a clip with very few
                    //notes over a "long" recommended time (or the reverse) could otherwise
                    //produce an unusably tiny or huge delay.
                    normalDelayMs = Clamp(perNoteMs, 200, 1500);
                    fastDelayMs = Clamp(normalDelayMs / 2, 100, 750);
                    effectiveBpm = Clamp((int)Math.Round(60000.0 / normalDelayMs), 40, 999);
                    source = "по времени прохождения этой песни";
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(difficulty))
            {
                string d = difficulty.Trim();
                int bpmForTier = 0;

                //Lowered by roughly a fifth from the original guesses (Super Easy 90, Easy 110,
                //Intermediate 130, Expert 170) after real-world testing across many songs
                //consistently found the old values played noticeably faster than intended -
                //not randomly off, but biased the same direction every time. This fallback still
                //carries the same characters-per-beat assumption described above though (there's
                //no per-song duration here to solve against instead), so it's inherently rougher
                //than the recommended-time path - it's only reached when that isn't available.
                if (string.Equals(d, "Super Easy", StringComparison.OrdinalIgnoreCase))
                {
                    bpmForTier = 72; source = "по сложности (Super Easy)";
                }
                else if (string.Equals(d, "Easy", StringComparison.OrdinalIgnoreCase))
                {
                    bpmForTier = 88; source = "по сложности (Easy)";
                }
                else if (string.Equals(d, "Intermediate", StringComparison.OrdinalIgnoreCase))
                {
                    bpmForTier = 104; source = "по сложности (Intermediate)";
                }
                else if (string.Equals(d, "Expert", StringComparison.OrdinalIgnoreCase))
                {
                    bpmForTier = 136; source = "по сложности (Expert)";
                }

                if (bpmForTier > 0)
                {
                    normalDelayMs = 60000 / bpmForTier;
                    fastDelayMs = normalDelayMs / 2;
                    effectiveBpm = bpmForTier;
                    return true;
                }
            }

            //normalDelayMs/fastDelayMs/effectiveBpm/source are already at their default
            //(0/0/0/null) values from the top of the method - neither return-true path above
            //was taken, so nothing overwrote them.
            return false;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        #endregion

        #region Fetching a single music sheet

        /// <summary>
        /// Downloads a virtualpiano.net music sheet page, extracts its title/artist/difficulty
        /// and raw notation, and converts the notation for this Autoplayer.
        /// </summary>
        /// <param name="speedMultiplier">
        /// A user-controlled correction factor (1.0 = no change) applied on top of whatever
        /// delay this method would otherwise suggest. No automatic per-song calibration can
        /// perfectly reproduce a real human performance's timing - a single averaged delay is
        /// fundamentally a simplification. This exists so the user can correct for whatever
        /// consistent bias they actually hear in-game (e.g. 0.85 if imported songs keep feeling
        /// a bit slow) without having to hand-recalculate Settings-tab delays after every import.
        /// </param>
        public static async Task<VirtualPianoImportResult> ImportFromUrlAsync(string url, double speedMultiplier = 1.0)
        {
            Uri uri = NormalizeSheetUrl(url);

            string html = await httpClient.GetStringAsync(uri).ConfigureAwait(false);

            string title = StripTags(ExtractBetween(html, "<h1", "</h1>")).Trim();
            if (title.Length == 0)
            {
                string[] segments = uri.Segments;
                title = segments.Length > 0
                    ? segments[segments.Length - 1].Trim('/').Replace('-', ' ')
                    : uri.ToString();
            }

            string artist = ExtractFirstGroup(html, "/artists/[^\"']+[\"'][^>]*>([^<]+)<");

            string plainText = HtmlToPlainText(html);
            //Virtual Piano states difficulty as a sentence ("This is an Intermediate song and
            //requires a lot of practice to play well.", "This is a Super Easy song which you can
            //also load and play on your mobile or tablet.", "This is an Expert song and aimed at
            //advanced users.") rather than as a "Difficulty: X" label, so the anchor has to be
            //"this is a/an X song", not the word "difficulty" (which doesn't actually appear
            //next to it on the page).
            string difficulty = ExtractFirstGroup(plainText, "[Tt]his is an?\\s+(Super Easy|Easy|Intermediate|Expert)\\s+song");

            //Some (not all) pages also state a community-verified completion time - where
            //present, this calibrates far more reliably than any BPM-based guess, since it's
            //solved directly from a known, real duration rather than assuming any particular
            //characters-per-beat ratio.
            int? recommendedTimeMs = ExtractRecommendedTimeMs(plainText);

            //Highest priority of all, where a GetSongBPM API key is configured: the real,
            //measured tempo of the actual recording, rather than anything derived from this
            //page. Still just one signal among several - a missing key, no match, or a failed
            //request all just mean this is skipped in favour of the next one below.
            int? realWorldBpm = await GetSongBpmClient.TryGetBpmAsync(
                Properties.Settings.Default.GetSongBpmApiKey, title, artist).ConfigureAwait(false);

            string rawNotation = ExtractNotationBlock(plainText);

            if (string.IsNullOrWhiteSpace(rawNotation))
            {
                throw new InvalidOperationException(
                    "Не удалось найти текст нот на этой странице. Возможно, разметка на virtualpiano.net " +
                    "изменилась, или это не страница с нотами.");
            }

            ConversionStats stats;
            string converted = ConvertNotation(rawNotation, out stats);

            int suggestedNormal, suggestedFast, suggestedBpm;
            string suggestionSource;
            bool speedApplied;
            if (realWorldBpm.HasValue)
            {
                suggestedBpm = realWorldBpm.Value;
                suggestedNormal = Clamp(60000 / suggestedBpm, 200, 1500);
                suggestedFast = Clamp(suggestedNormal / 2, 100, 750);
                suggestionSource = "по реальному темпу записи (GetSongBPM.com)";
                speedApplied = true;
            }
            else
            {
                speedApplied = TryGetSuggestedDelays(difficulty, recommendedTimeMs, stats,
                    out suggestedNormal, out suggestedFast, out suggestedBpm, out suggestionSource);
            }

            ApplySpeedMultiplier(speedMultiplier, ref speedApplied, ref suggestedNormal, ref suggestedFast, ref suggestedBpm, ref suggestionSource);

            //Whether or not we could calibrate a suggestion for the Settings tab, the pause
            //characters used above still need real delay times registered so they aren't
            //silently dropped as unknown characters when the song is played.
            RegisterPauseDelays();

            return new VirtualPianoImportResult
            {
                Title = title,
                Artist = artist,
                Difficulty = difficulty,
                SourceUrl = uri.ToString(),
                RawNotation = rawNotation,
                ConvertedNotation = converted,
                ChordCount = stats.ChordCount,
                FastGroupCount = stats.FastGroupCount,
                PauseCount = stats.PauseCount,
                SkippedCharacterCount = stats.SkippedCharacterCount,
                SpeedSuggestionApplied = speedApplied,
                SuggestedNormalDelayMs = suggestedNormal,
                SuggestedFastDelayMs = suggestedFast,
                SuggestedBpm = suggestedBpm,
                SuggestedDelaySource = suggestionSource
            };
        }

        /// <summary>
        /// Converts Virtual Piano-style notation the user has pasted in directly - copied by
        /// hand from wherever they found it (a forum post, a community-run board, anywhere) -
        /// through the same conversion and calibration pipeline as a virtualpiano.net URL
        /// import.
        ///
        /// If the person copied more than just the bare notation - e.g. the whole page, or at
        /// least the surrounding text that states the song's difficulty and/or Virtual Piano's
        /// own recommended completion time - this is detected automatically here, the same way
        /// it would be from a direct URL import, and used in preference to the manually-picked
        /// difficulty. The dropdown only actually matters as a fallback for when neither of
        /// those was found in what was pasted.
        /// </summary>
        public static VirtualPianoImportResult ImportFromPastedText(string pastedText, string manuallyPickedDifficulty, double speedMultiplier = 1.0)
        {
            if (string.IsNullOrWhiteSpace(pastedText))
            {
                throw new ArgumentException("Вставьте текст нот, которые нужно преобразовать.");
            }

            //These look for the exact same phrases a virtualpiano.net page uses, so they'll only
            //actually find anything if that surrounding text was part of what got pasted in -
            //otherwise they simply return null/empty, same as scraping a page that never had
            //this text either.
            string detectedDifficulty = ExtractFirstGroup(pastedText, "[Tt]his is an?\\s+(Super Easy|Easy|Intermediate|Expert)\\s+song");
            int? recommendedTimeMs = ExtractRecommendedTimeMs(pastedText);
            string difficulty = !string.IsNullOrWhiteSpace(detectedDifficulty) ? detectedDifficulty : manuallyPickedDifficulty;

            //Also strip out the sentences those two just matched against, and anything else that
            //looks like page furniture rather than notation, before conversion - so text copied
            //from the whole page doesn't end up trying to play the word "song" as three notes.
            string notationOnly = ExtractNotationBlock(pastedText);
            string rawKeys = !string.IsNullOrWhiteSpace(notationOnly) ? notationOnly : pastedText;

            ConversionStats stats;
            string converted = ConvertNotation(rawKeys, out stats);

            int suggestedNormal, suggestedFast, suggestedBpm;
            string suggestionSource;
            bool speedApplied = TryGetSuggestedDelays(difficulty, recommendedTimeMs, stats,
                out suggestedNormal, out suggestedFast, out suggestedBpm, out suggestionSource);

            ApplySpeedMultiplier(speedMultiplier, ref speedApplied, ref suggestedNormal, ref suggestedFast, ref suggestedBpm, ref suggestionSource);

            RegisterPauseDelays();

            return new VirtualPianoImportResult
            {
                Title = "Вставленные ноты",
                Artist = "",
                Difficulty = difficulty,
                SourceUrl = null,
                RawNotation = rawKeys,
                ConvertedNotation = converted,
                ChordCount = stats.ChordCount,
                FastGroupCount = stats.FastGroupCount,
                PauseCount = stats.PauseCount,
                SkippedCharacterCount = stats.SkippedCharacterCount,
                SpeedSuggestionApplied = speedApplied,
                SuggestedNormalDelayMs = suggestedNormal,
                SuggestedFastDelayMs = suggestedFast,
                SuggestedBpm = suggestedBpm,
                SuggestedDelaySource = suggestionSource
            };
        }

        /// <summary>
        /// Scales the calibrated delay by the user's own speed multiplier, if they've set one
        /// away from 1.0x - or, if nothing could be calibrated at all, applies the multiplier to
        /// whatever is currently configured instead, so an explicit correction is still honoured
        /// even with no song-specific signal to scale.
        /// </summary>
        private static void ApplySpeedMultiplier(double speedMultiplier, ref bool speedApplied,
            ref int suggestedNormal, ref int suggestedFast, ref int suggestedBpm, ref string suggestionSource)
        {
            bool multiplierActive = Math.Abs(speedMultiplier - 1.0) > 0.001;
            if (!multiplierActive)
                return;

            if (speedApplied)
            {
                suggestedNormal = Clamp((int)Math.Round(suggestedNormal * speedMultiplier), 30, 800);
                suggestedFast = Clamp((int)Math.Round(suggestedFast * speedMultiplier), 15, 400);
                suggestedBpm = Clamp((int)Math.Round(60000.0 / suggestedNormal), 20, 1999);
                suggestionSource += $", с вашим множителем x{speedMultiplier:0.00}";
            }
            else
            {
                suggestedNormal = Clamp((int)Math.Round(Autoplayer.DelayAtNormalSpeed * speedMultiplier), 30, 800);
                suggestedFast = Clamp((int)Math.Round(Autoplayer.DelayAtFastSpeed * speedMultiplier), 15, 400);
                suggestedBpm = Clamp((int)Math.Round(60000.0 / suggestedNormal), 20, 1999);
                suggestionSource = $"вашего множителя скорости (x{speedMultiplier:0.00})";
                speedApplied = true;
            }
        }

        /// <summary>
        /// Extracts Virtual Piano's own community-verified "recommended time to play this music
        /// sheet", where the page states one. Every word boundary here tolerates one-or-more
        /// whitespace characters rather than requiring exactly one literal space, since a literal
        /// single space would silently fail to match if an HTML tag happened to sit at one of
        /// these word junctions in the source (HtmlToPlainText replaces tags with a space, so a
        /// tag plus a pre-existing natural space at the same spot produces two consecutive spaces
        /// there).
        /// </summary>
        private static int? ExtractRecommendedTimeMs(string plainText)
        {
            Match m = Regex.Match(plainText,
                "recommended\\s+time\\s+to\\s+play\\s+this\\s+music\\s+sheet\\s+is\\s+(\\d{1,2}):(\\d{2})",
                RegexOptions.IgnoreCase);
            if (!m.Success)
                return null;

            int minutes, seconds;
            if (!int.TryParse(m.Groups[1].Value, out minutes) || !int.TryParse(m.Groups[2].Value, out seconds))
                return null;

            return (minutes * 60 + seconds) * 1000;
        }

        private static Uri NormalizeSheetUrl(string url)
        {
            string trimmed = (url ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("Введите ссылку на virtualpiano.net.");

            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = "https://" + trimmed;
            }

            Uri uri;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri))
                throw new ArgumentException("Это не похоже на настоящую ссылку.");

            if (uri.Host.IndexOf("virtualpiano.net", StringComparison.OrdinalIgnoreCase) < 0)
                throw new ArgumentException("Введите ссылку именно на virtualpiano.net.");

            return uri;
        }

        #endregion

        #region Searching

        /// <summary>
        /// Searches virtualpiano.net for music sheets matching the given query. Tries the
        /// WordPress REST search API first (clean JSON, most reliable), and falls back to
        /// scraping the site's own search results page if that returns nothing usable.
        /// </summary>
        public static async Task<List<VirtualPianoSearchResult>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<VirtualPianoSearchResult>();

            List<VirtualPianoSearchResult> results = null;

            try
            {
                results = await SearchViaRestApiAsync(query).ConfigureAwait(false);
            }
            catch
            {
                results = null;
            }

            if (results == null || results.Count == 0)
            {
                try
                {
                    results = await SearchViaHtmlAsync(query).ConfigureAwait(false);
                }
                catch
                {
                    results = results ?? new List<VirtualPianoSearchResult>();
                }
            }

            return results;
        }

        private static async Task<List<VirtualPianoSearchResult>> SearchViaRestApiAsync(string query)
        {
            string url = $"{BaseUrl}/wp-json/wp/v2/search?search={Uri.EscapeDataString(query)}&per_page=20";
            string json = await httpClient.GetStringAsync(url).ConfigureAwait(false);

            List<VirtualPianoSearchResult> results = new List<VirtualPianoSearchResult>();

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            object parsed = serializer.DeserializeObject(json);
            object[] items = parsed as object[];
            if (items == null)
                return results;

            foreach (object itemObj in items)
            {
                Dictionary<string, object> item = itemObj as Dictionary<string, object>;
                if (item == null)
                    continue;

                object urlObj, titleObj;
                item.TryGetValue("url", out urlObj);
                item.TryGetValue("title", out titleObj);

                string itemUrl = urlObj as string;
                string itemTitle = titleObj as string;

                if (string.IsNullOrEmpty(itemUrl) ||
                    itemUrl.IndexOf("/music-sheet/", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                results.Add(new VirtualPianoSearchResult
                {
                    Title = WebUtility.HtmlDecode(itemTitle ?? itemUrl),
                    Url = itemUrl,
                    Artist = string.Empty
                });
            }

            return results;
        }

        private static async Task<List<VirtualPianoSearchResult>> SearchViaHtmlAsync(string query)
        {
            string url = $"{BaseUrl}/?s={Uri.EscapeDataString(query)}";
            string html = await httpClient.GetStringAsync(url).ConfigureAwait(false);

            List<VirtualPianoSearchResult> results = new List<VirtualPianoSearchResult>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(
                html,
                "<a[^>]+href=[\"']([^\"']*?/music-sheet/[^\"']+)[\"'][^>]*>(.*?)</a>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string href = m.Groups[1].Value;
                string text = WebUtility.HtmlDecode(StripTags(m.Groups[2].Value)).Trim();

                if (text.Length == 0 || !seen.Add(href))
                    continue;

                results.Add(new VirtualPianoSearchResult { Title = text, Url = href, Artist = string.Empty });
            }

            return results;
        }

        #endregion

        #region Genre browsing

        /// <summary>
        /// A starting set of genres/categories for the Song Selection tab, limited to slugs
        /// confirmed to exist by directly fetching virtualpiano.net's own category/tag pages and
        /// navigation links, rather than guessed. virtualpiano.net has around 25 categories and
        /// 140 tags in total, so this is a useful subset rather than the complete list - there is
        /// no reliably scrapable "list every genre" page to build the full set from.
        /// </summary>
        public static List<VirtualPianoGenre> GetKnownGenres()
        {
            return new List<VirtualPianoGenre>
            {
                new VirtualPianoGenre { DisplayName = "Поп", UrlPath = "music-sheet-categories/pop" },
                new VirtualPianoGenre { DisplayName = "Классика", UrlPath = "music-sheet-categories/classical" },
                new VirtualPianoGenre { DisplayName = "Рок", UrlPath = "music-sheet-categories/rock" },
                new VirtualPianoGenre { DisplayName = "Дэнс", UrlPath = "music-sheet-categories/dance" },
                new VirtualPianoGenre { DisplayName = "Инди", UrlPath = "music-sheet-categories/indie" },
                new VirtualPianoGenre { DisplayName = "Из фильмов", UrlPath = "music-sheet-categories/songs-from-movies" },
                new VirtualPianoGenre { DisplayName = "Из игр", UrlPath = "music-sheet-categories/songs-from-games" },
                new VirtualPianoGenre { DisplayName = "Рождественские", UrlPath = "music-sheet-categories/christmas-songs" },
                new VirtualPianoGenre { DisplayName = "Из сериалов", UrlPath = "music-sheet-categories/songs-from-tv" },
                new VirtualPianoGenre { DisplayName = "Манга", UrlPath = "music-sheet-categories/manga" },
                new VirtualPianoGenre { DisplayName = "Аниме", UrlPath = "music-sheet-tag/anime" },
                new VirtualPianoGenre { DisplayName = "Альтернатива", UrlPath = "music-sheet-tag/alternative" },
            };
        }

        //Matches a song-sheet link followed (within a generous window, to span the thumbnail,
        //notation preview, and level/length/difficulty text that sits between them on a genre
        //page) by two headings in a row - the title, then the artist. The heading level isn't
        //assumed to be any specific number, since that's markup this program has never directly
        //observed and could vary or change.
        private static readonly Regex CategorySongRegex = new Regex(
            "href=\"(https://virtualpiano\\.net/music-sheet/[^\"]+)\"[\\s\\S]{0,600}?<h[1-6][^>]*>([^<]+)</h[1-6]>\\s*<h[1-6][^>]*>([^<]+)</h[1-6]>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Fetches songs listed on a genre/category page, following pagination (?pages=N) up to
        /// maxPages pages, stopping early if a page yields nothing (either the genre has fewer
        /// pages than that, or the page's markup didn't match - either way there's no point
        /// requesting further pages). This is heuristic, the same way SearchViaHtmlAsync is:
        /// there is no documented API for this, so it works by pattern rather than a guaranteed
        /// stable structure.
        /// </summary>
        public static async Task<List<VirtualPianoCategorySong>> GetSongsInGenreAsync(string genreUrlPath, int maxPages = 3)
        {
            List<VirtualPianoCategorySong> results = new List<VirtualPianoCategorySong>();
            HashSet<string> seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int page = 1; page <= maxPages; page++)
            {
                string url = page == 1
                    ? $"{BaseUrl}/{genreUrlPath}/"
                    : $"{BaseUrl}/{genreUrlPath}/?pages={page}";

                string html;
                try
                {
                    html = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                }
                catch
                {
                    //No more pages, or a transient error - stop here rather than throw, since we
                    //may already have useful results from earlier pages.
                    break;
                }

                int foundOnThisPage = 0;
                foreach (Match m in CategorySongRegex.Matches(html))
                {
                    string songUrl = m.Groups[1].Value;
                    string title = WebUtility.HtmlDecode(StripTags(m.Groups[2].Value)).Trim();
                    string artist = WebUtility.HtmlDecode(StripTags(m.Groups[3].Value)).Trim();

                    if (songUrl.Length == 0 || title.Length == 0 || !seenUrls.Add(songUrl))
                        continue;

                    results.Add(new VirtualPianoCategorySong { Title = title, Artist = artist, Url = songUrl });
                    foundOnThisPage++;
                }

                if (foundOnThisPage == 0)
                    break;
            }

            return results;
        }

        /// <summary>
        /// Distinct artist names found among the given songs, sorted alphabetically. Songs with
        /// no artist attributed on the listing page (rare, but the markup this is parsed from
        /// isn't guaranteed) are grouped under a placeholder rather than dropped.
        /// </summary>
        public static List<string> GetDistinctArtists(IEnumerable<VirtualPianoCategorySong> songs)
        {
            return songs
                .Select(s => string.IsNullOrWhiteSpace(s.Artist) ? "(автор не указан)" : s.Artist.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion

        #region HTML helpers

        private static string StripTags(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;
            //Replacing a tag with nothing can silently glue together the text on either side
            //of it (e.g. "<span>tw</span><span>yw</span>" becoming "twyw" instead of "tw yw"),
            //which corrupts Virtual Piano's spacing-based grouping/pause information. A space
            //is a safe stand-in either way: if there was already whitespace there, this just
            //adds an extra space (harmless - see the collapse below); if there wasn't, it stops
            //two separate tokens from being fused into one.
            string spaced = Regex.Replace(html, "<[^>]*>", " ");
            //Keep the "one vs two extra spaces" distinction pause-tier detection relies on, but
            //don't let runs longer than that keep growing from repeated tag removals.
            return Regex.Replace(spaced, "[ \t]{3,}", "  ");
        }

        private static string ExtractBetween(string html, string startTag, string endTag)
        {
            int start = html.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            int contentStart = html.IndexOf('>', start);
            if (contentStart < 0)
                return string.Empty;
            contentStart++;

            int end = html.IndexOf(endTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                return string.Empty;

            return html.Substring(contentStart, end - contentStart);
        }

        private static string ExtractFirstGroup(string html, string pattern)
        {
            Match m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!m.Success)
                return string.Empty;
            return (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
        }

        /// <summary>
        /// A small HTML-to-text conversion: removes script/style blocks, turns block-level
        /// tags into line breaks, strips remaining tags and decodes entities. This deliberately
        /// doesn't try to be a full HTML parser - virtualpiano.net's markup isn't guaranteed to
        /// stay the same, so ExtractNotationBlock below looks for the notation by what it looks
        /// like rather than by a specific CSS class or element id.
        /// </summary>
        private static string HtmlToPlainText(string html)
        {
            string text = Regex.Replace(html, "<script[^>]*>[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<style[^>]*>[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<(br|/p|/div|/li|/h[1-6])\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = StripTags(text);
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, "[ \t]+\n", "\n");
            text = Regex.Replace(text, "\n{3,}", "\n\n");
            return text;
        }

        /// <summary>
        /// Finds the block of actual note text on a rendered music sheet page. Virtual Piano
        /// doesn't expose a documented way to fetch just the notation, so this looks for it the
        /// same way a person reading the page would: it's the last run of paragraphs, made
        /// almost entirely of the letters/digits/brackets/pipes Virtual Piano notation uses,
        /// that appears before the rating/comments section near the bottom of the page.
        /// </summary>
        private static string ExtractNotationBlock(string plainText)
        {
            int cutoff = IndexOfEarliest(plainText, new[] { "Rate This Music Sheet", "Submit Feedback", "Comments" });
            string candidateArea = cutoff > 0 ? plainText.Substring(0, cutoff) : plainText;

            string[] paragraphs = Regex.Split(candidateArea, "\n\\s*\n");

            List<string> chosen = new List<string>();
            //Scan backwards, collecting consecutive paragraphs that look like notation, and
            //stop as soon as one doesn't (e.g. the difficulty/type info box above the notes).
            for (int i = paragraphs.Length - 1; i >= 0; i--)
            {
                string p = paragraphs[i].Trim();
                if (p.Length == 0)
                    continue;

                if (LooksLikeNotation(p))
                {
                    chosen.Insert(0, p);
                }
                else if (chosen.Count > 0)
                {
                    break;
                }
            }

            return string.Join("\n\n", chosen);
        }

        private static int IndexOfEarliest(string text, string[] markers)
        {
            int best = -1;
            foreach (string marker in markers)
            {
                int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (best < 0 || idx < best))
                    best = idx;
            }
            return best;
        }

        private static bool LooksLikeNotation(string paragraph)
        {
            if (paragraph.Length < 8)
                return false;

            int relevant = 0;
            int total = 0;
            foreach (char c in paragraph)
            {
                if (char.IsWhiteSpace(c))
                    continue;
                total++;
                if (char.IsLetterOrDigit(c) || "[]|!@#$%^&*()".IndexOf(c) >= 0)
                    relevant++;
            }

            if (total < 8)
                return false;

            return (double)relevant / total > 0.9;
        }

        #endregion
    }
}
