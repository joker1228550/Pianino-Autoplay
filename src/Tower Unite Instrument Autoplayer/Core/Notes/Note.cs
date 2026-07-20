using System.Threading;
using WindowsInput;
using WindowsInput.Native;

namespace Tower_Unite_Instrument_Autoplayer.Core
{
    /// <summary>
    /// This class defines a Note
    /// This is where the behaviour of the note is defined
    /// The Key property holds information about which key
    /// on the keyboard the note corrosponds to
    /// </summary>
    public class Note : INote
    {
        public VirtualKeyCode NoteToPlay { get; private set; }
        public char Character { get; private set; }
        public bool IsHighNote { get; private set; }

        public Note(char character, VirtualKeyCode note, bool isHighNote)
        {
            NoteToPlay = note;
            Character = character;
            IsHighNote = isHighNote;
        }

        public void Play()
        {
            //This method is used until a better solution is found. This will NOT play black keys :(
            //SendKeys.SendWait(Character.ToString());

            //EXPERIMENTAL SOLUTION
            //This method is a better solution as you can define a delay. However, this may result in unexpected behaviour should the program be terminated before KeyUp is run!
            if (IsHighNote)
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyDown(VirtualKeyCode.LSHIFT);
                //Just long enough for the game to register Shift as already held before the
                //note key's own KeyDown arrives (matches the delay MultiNote already uses for
                //the same purpose). This used to be 50ms, which is far more than needed and was
                //the whole source of high notes having a noticeably longer lead-in than low
                //notes before they even started sounding.
                Thread.Sleep(8);
                Autoplayer.SharedInputSimulator.Keyboard.KeyDown(NoteToPlay);
                Thread.Sleep(50);
                Autoplayer.SharedInputSimulator.Keyboard.KeyUp(NoteToPlay);
                Autoplayer.SharedInputSimulator.Keyboard.KeyPress(VirtualKeyCode.LSHIFT);
            }
            else
            {
                Autoplayer.SharedInputSimulator.Keyboard.KeyDown(NoteToPlay);
                Thread.Sleep(50);
                Autoplayer.SharedInputSimulator.Keyboard.KeyUp(NoteToPlay);
            }
        }

        public void Stop()
        {
            Autoplayer.SharedInputSimulator.Keyboard.KeyPress(VirtualKeyCode.LSHIFT);
            Autoplayer.SharedInputSimulator.Keyboard.KeyUp(NoteToPlay);
        }

        public override string ToString()
        {
            return Character.ToString();
        }

        public override bool Equals(object obj)
        {
            Note other = obj as Note;

            if(other != null)
            {
                if (Character == other.Character && IsHighNote == other.IsHighNote)
                    return true;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return Character.GetHashCode();
        }
    }
}
