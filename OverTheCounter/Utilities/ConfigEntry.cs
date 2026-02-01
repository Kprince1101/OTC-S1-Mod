using MelonLoader;

namespace OverTheCounter.Utilities
{
    public class ConfigEntry<T>
    {
        private readonly MelonPreferences_Entry<T> _entry;
        private T _override;
        private bool _hasOverride;

        public ConfigEntry(MelonPreferences_Entry<T> entry)
        {
            _entry = entry;
        }

        public T Value => _hasOverride ? _override : _entry.Value;

        public MelonPreferences_Entry<T> RawEntry => _entry;

        public void SetOverride(T value)
        {
            _override = value;
            _hasOverride = true;
        }

        public void ClearOverride()
        {
            _hasOverride = false;
            _override = default;
        }
    }
}
