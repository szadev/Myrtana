using G3.Unity;
using Myrtana.Core;

namespace Myrtana.Unity
{
    /// <summary>
    /// Save section "host.weather": every weather environment's schedule (current and next state, fade start and length on
    /// the game clock; <see cref="G3WeatherDirector.SaveSchedule"/>), so a game saved in the rain loads in the rain.
    /// Unknown environments or states in an old save let the scheduler pick anew.
    /// </summary>
    public sealed class WeatherSaveSection : ISaveParticipant
    {
        public string Section => "host.weather";
        public int Version => 1;

        public void Write(ISaveWriter w) => w.WriteString(G3WeatherDirector.Instance != null ? G3WeatherDirector.Instance.SaveSchedule() : null);

        public void Read(ISaveReader r, int version)
        {
            string saved = r.ReadString();
            (G3WeatherDirector.Instance != null ? G3WeatherDirector.Instance : G3WeatherDirector.Ensure())?.LoadSchedule(saved);
        }
    }
}
