using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiNote = Melanchall.DryWetMidi.Interaction.Note;

namespace Tower_Unite_Instrument_Autoplayer.Core
{
    public class MidiImportResult
    {
        public string ConvertedNotation { get; set; }
        public int NoteCount { get; set; }
        public int ChordCount { get; set; }
        public int SkippedOutOfRangeCount { get; set; }
        public int SuggestedNormalDelayMs { get; set; }
        public int SuggestedFastDelayMs { get; set; }
        public int SuggestedBpm { get; set; }
    }

    /// <summary>
    /// Converts a Standard MIDI File into this program's own note notation, using
    /// Melanchall.DryWetMidi to read the actual note pitches and timing rather than attempting
    /// to read them from a picture of sheet music - that would need genuine optical music
    /// recognition (reading staves, clefs, note heads, durations, ties, dynamics from a raster
    /// image), which is a hard, specialised computer-vision problem this program has no
    /// reliable way to do. A MIDI file, by contrast, already states every note's pitch and exact
    /// timing as plain structured data, which is what actually makes this conversion tractable.
    ///
    /// The pitch-to-character mapping below is reconstructed from Virtual Piano's own published
    /// facts (their FAQ: a "5 Octave Piano Keyboard with 61 keys; 36 white and 25 black", keys
    /// spanning "1" to "m", black keys following the same skip-pattern as a real piano - no sharp
    /// immediately after E or B) rather than a directly-observed, key-by-key chart, since no such
    /// chart is published. It's internally consistent with those documented facts, but hasn't
    /// been checked note-by-note against the live site - if a converted song sounds transposed
    /// (right rhythm, wrong pitch register), that's the thing to double check first.
    /// </summary>
    public static class MidiImporter
    {
        //Virtual Piano's own FAQ states the keys span "1" to "m" on a QWERTY keyboard - this is
        //that exact span, in reading order (digit row, then the three letter rows top to bottom),
        //which is also the order this program already uses for the 36-key layout throughout.
        private const string WhiteKeyChars = "1234567890qwertyuiopasdfghjklzxcvbnm";

        //Shift-symbol equivalents for the 10 digit characters, since digits have no uppercase
        //form of their own - standard US keyboard shift row. This matches how this program's
        //existing Virtual Piano notation already represents these (see VirtualPianoImporter's
        //DigitSymbolMap) - that table's job is translating these same symbols for the Danish
        //keyboard actually used to simulate keypresses, which happens later in the existing
        //pipeline and isn't this converter's concern.
        private const string DigitShiftChars = "!@#$%^&*()";

        private static readonly int[] NaturalSemitoneFromC = { 0, 2, 4, 5, 7, 9, 11 }; // C D E F G A B
        //A real piano has no black key immediately after E or B - this is that same skip pattern,
        //aligned to NaturalSemitoneFromC above (index 2 = E, index 6 = B).
        private static readonly bool[] HasSharpAbove = { true, true, false, true, true, true, false };

        //Below this MIDI note number, or above the top of the 36-key range, a note has no
        //corresponding Virtual Piano key at the default (non-transposed) octave and is skipped
        //rather than silently clamped to the wrong pitch.
        private static Dictionary<int, char> BuildPitchToCharMap()
        {
            Dictionary<int, char> map = new Dictionary<int, char>();
            for (int i = 0; i < WhiteKeyChars.Length; i++)
            {
                int octave = 2 + i / 7;
                int posInOctave = i % 7;
                int midiNote = 12 * (octave + 1) + NaturalSemitoneFromC[posInOctave];

                char lowChar = WhiteKeyChars[i];
                map[midiNote] = lowChar;

                if (HasSharpAbove[posInOctave])
                {
                    int digitIndex = "1234567890".IndexOf(lowChar);
                    char highChar = digitIndex >= 0 ? DigitShiftChars[digitIndex] : char.ToUpper(lowChar);
                    map[midiNote + 1] = highChar;
                }
            }
            return map;
        }

        /// <summary>
        /// Reads a Standard MIDI File and converts its notes into this program's notation:
        /// notes starting within a few milliseconds of each other become a [chord], the typical
        /// gap between one note/chord and the next becomes the baseline "normal" delay, shorter
        /// gaps become touching fast runs, and longer gaps become this program's own pause
        /// characters - scaled relative to that same baseline, the same as everywhere else in
        /// this program pause characters are used.
        /// </summary>
        public static MidiImportResult ConvertMidiFile(string filePath, double speedMultiplier = 1.0, int octaveOffset = 0)
        {
            MidiFile midiFile = MidiFile.Read(filePath);
            TempoMap tempoMap = midiFile.GetTempoMap();

            List<MidiNote> notes = midiFile.GetNotes()
                .OrderBy(n => n.Time)
                .ToList();

            if (notes.Count == 0)
            {
                throw new InvalidOperationException("В этом MIDI-файле не найдено нот.");
            }

            Dictionary<int, char> pitchMap = BuildPitchToCharMap();
            int semitoneOffset = octaveOffset * 12;

            //Group notes into chords: anything starting within a few milliseconds of the first
            //note in a group counts as "simultaneous" for this program's purposes, since real
            //MIDI performances/exports are rarely perfectly aligned to the millisecond.
            const int chordWindowMs = 40;

            List<double> clusterStartMs = new List<double>();
            List<List<int>> clusterPitches = new List<List<int>>();
            int skippedOutOfRange = 0;

            foreach (MidiNote note in notes)
            {
                double startMs = note.TimeAs<MetricTimeSpan>(tempoMap).TotalMicroseconds / 1000.0;

                //Applied before the range check (not after), so an offset can bring an
                //originally out-of-range note into range instead of it always being skipped
                //regardless of the offset the person dialled in.
                int shiftedNoteNumber = note.NoteNumber + semitoneOffset;

                if (!pitchMap.ContainsKey(shiftedNoteNumber))
                {
                    skippedOutOfRange++;
                    continue;
                }

                if (clusterStartMs.Count > 0 && startMs - clusterStartMs[clusterStartMs.Count - 1] <= chordWindowMs)
                {
                    clusterPitches[clusterPitches.Count - 1].Add(shiftedNoteNumber);
                }
                else
                {
                    clusterStartMs.Add(startMs);
                    clusterPitches.Add(new List<int> { shiftedNoteNumber });
                }
            }

            if (clusterStartMs.Count == 0)
            {
                throw new InvalidOperationException(
                    "Ни одна нота из этого файла не попадает в диапазон клавиш Virtual Piano (36 " +
                    "белых/25 чёрных клавиш от C2). Попробуйте транспонировать MIDI-файл в этот диапазон.");
            }

            //The typical (median) gap between one cluster and the next becomes the baseline
            //"normal" delay this song is calibrated to - adapts to this specific piece's actual
            //note density rather than assuming a fixed subdivision of the tempo, since two songs
            //at the same BPM can still have very different typical note-to-note spacing.
            List<double> gaps = new List<double>();
            for (int i = 1; i < clusterStartMs.Count; i++)
            {
                gaps.Add(clusterStartMs[i] - clusterStartMs[i - 1]);
            }
            gaps.Sort();
            double medianGapMs = gaps.Count > 0 ? gaps[gaps.Count / 2] : 250;
            if (medianGapMs < 60) medianGapMs = 60; //guard against a degenerate near-zero median

            int normalDelayMs = Clamp((int)Math.Round(medianGapMs * speedMultiplier), 60, 1500);
            int fastDelayMs = Clamp(normalDelayMs / 2, 30, 750);
            int bpm = Clamp((int)Math.Round(60000.0 / normalDelayMs), 20, 999);

            StringBuilder sb = new StringBuilder();
            int chordCount = 0;

            for (int i = 0; i < clusterPitches.Count; i++)
            {
                List<int> pitches = clusterPitches[i].Distinct().OrderBy(p => p).ToList();
                string token;
                if (pitches.Count == 1)
                {
                    token = pitchMap[pitches[0]].ToString();
                }
                else
                {
                    token = "[" + new string(pitches.Select(p => pitchMap[p]).ToArray()) + "]";
                    chordCount++;
                }

                if (i == 0)
                {
                    sb.Append(token);
                    continue;
                }

                double gap = clusterStartMs[i] - clusterStartMs[i - 1];
                if (gap <= medianGapMs * 0.6)
                {
                    //Touching - this program (like Virtual Piano's own notation) already treats
                    //directly adjacent characters with no separator as a fast run.
                    sb.Append(token);
                }
                else if (gap <= medianGapMs * 1.5)
                {
                    sb.Append(' ').Append(token);
                }
                else if (gap <= medianGapMs * 3)
                {
                    sb.Append(VirtualPianoImporter.ShortPauseChar).Append(token);
                }
                else if (gap <= medianGapMs * 6)
                {
                    sb.Append(VirtualPianoImporter.LongPauseChar).Append(token);
                }
                else
                {
                    sb.Append(VirtualPianoImporter.ExtendedPauseChar).Append(token);
                }
            }

            return new MidiImportResult
            {
                ConvertedNotation = sb.ToString(),
                NoteCount = notes.Count - skippedOutOfRange,
                ChordCount = chordCount,
                SkippedOutOfRangeCount = skippedOutOfRange,
                SuggestedNormalDelayMs = normalDelayMs,
                SuggestedFastDelayMs = fastDelayMs,
                SuggestedBpm = bpm
            };
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
