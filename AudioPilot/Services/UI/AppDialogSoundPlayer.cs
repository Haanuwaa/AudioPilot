using System.Media;

namespace AudioPilot.Services.UI
{
    internal interface IAppDialogSoundPlayer
    {
        void Play(AppDialogKind kind);
    }

    internal sealed class WindowsAppDialogSoundPlayer : IAppDialogSoundPlayer
    {
        private readonly Action<SystemSound> _play;

        internal WindowsAppDialogSoundPlayer(Action<SystemSound>? play = null)
        {
            _play = play ?? (static sound => sound.Play());
        }

        public void Play(AppDialogKind kind)
        {
            SystemSound sound = kind switch
            {
                AppDialogKind.Information or AppDialogKind.Success => SystemSounds.Asterisk,
                AppDialogKind.Warning => SystemSounds.Exclamation,
                AppDialogKind.Error => SystemSounds.Hand,
                AppDialogKind.Question => SystemSounds.Question,
                _ => SystemSounds.Asterisk,
            };

            _play(sound);
        }
    }
}
