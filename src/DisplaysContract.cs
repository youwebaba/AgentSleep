namespace AgentSleep
{
    internal sealed class DisplayTarget
    {
        public string Id, Label;
        public bool Active, Available, IsVirtual, IsPhysical;
    }

    internal interface IDisplays
    {
        DisplayTarget[] Discover();
        byte[] Capture(string selectedId = null);
        void Validate(byte[] baseline, string id);
        void Extend(byte[] baseline, string id);
        void Isolate(string id);
        bool IsOnly(string id);
        string Restore(byte[] baseline);
    }
}
