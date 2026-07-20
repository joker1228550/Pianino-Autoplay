using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WindowsInput;
using WindowsInput.Native;

namespace Tower_Unite_Instrument_Autoplayer.Core
{
    /// <summary>
    /// This class defines a MultiNote
    /// A MultiNote is a collection of notes to be played at once
    /// or, at least close to "at once".
    ///
    /// A MultiNote can contain a mix of high notes (played while holding Shift)
    /// and low notes (played without Shift) at the same time. This is achieved by
    /// pressing the low notes first, then holding Shift down and pressing the high
    /// notes while Shift is held, since the game only reacts to the KeyDown event
    /// and not to Shift being toggled while a key is already held.
    /// </summary>
    public class MultiNote : INote
    {
        public Note[] Notes { get; private set; }

        /// <summary>
        /// True if every note in this MultiNote is a high note.
        /// Kept for backwards compatibility with anything that inspects this property.
        /// </summary>
        public bool IsHighNote { get; private set; }

        /// <summary>
        /// True if this MultiNote contains at least one high note and at least one low note.
        /// </summary>
        public bool IsMixedNote { get; private set; }

        public MultiNote(Note[] notes)
        {
            Notes = notes;
            IsHighNote = notes.Length > 0 && notes.All(n => n.IsHighNote);
            IsMixedNote = notes.Any(n => n.IsHighNote) && notes.Any(n => !n.IsHighNote);
        }

        //Kept for backwards compatibility with any external callers using the old signature.
        //The isHighNote parameter is now ignored since each Note already knows whether it is high or low.
        public MultiNote(Note[] notes, bool isHighNote) : this(notes)
        {
        }

        public void Play()
        {
            //EXPERIMENTAL SOLUTION
            //This method is a better solution as you can define a delay. However, this may result in unexpected behaviour should the program be terminated before KeyUp is run!
            List<Note> lowNotes = Notes.Where(n => !n.IsHighNote).ToList();
            List<Note> highNotes = Notes.Where(n => n.IsHighNote).ToList();

            //Press the low (non-shifted) notes down first
            foreach (Note note in lowNotes)
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyDown(note.NoteToPlay);
            }

            if (highNotes.Count > 0)
            {
                if (lowNotes.Count > 0)
                {
                    Thread.Sleep(8);
                }

                //Hold Shift and press down the high notes while it is held
                Autoplayer.SharedInputSimulator.Keyboard.KeyDown(VirtualKeyCode.LSHIFT);
                Thread.Sleep(8);
                foreach (Note note in highNotes)
                {
                    Autoplayer.SharedInputSimulator.Keyboard.KeyDown(note.NoteToPlay);
                }
            }

            Thread.Sleep(8);

            //Release everything again
            foreach (Note note in highNotes)
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyUp(note.NoteToPlay);
            }

            if (highNotes.Count > 0)
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyUp(VirtualKeyCode.LSHIFT);
            }

            foreach (Note note in lowNotes)
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyUp(note.NoteToPlay);
            }

            //foreach (Note note in Notes)
            //{
            //    note.Play();
            //}
        }

        public void Stop()
        {
            Autoplayer.SharedInputSimulator.Keyboard.KeyUp(VirtualKeyCode.LSHIFT);
            foreach (Note note in Notes)
            {
                note.Stop();
            }
        }

        public override string ToString()
        {
            string str = "";
            foreach (Note note in Notes)
            {
                str += note.ToString();
            }
            return str;
        }
    }
}
