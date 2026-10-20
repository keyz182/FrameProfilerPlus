namespace FrameProfilerPlus.Ticks
{
    // Each probe times one kind of tick code and keeps its own nesting stack, so groups of different probes
    // are separate views of the same time, not parts of one total.
    public enum TickProbe
    {
        Thing,
        Driver,
        Comp,
        WorkGiver,
        Stat,
    }

    // A frame of the selection and how many game ticks ran in it (FrameSnapshot.frameIndex, tickDelta).
    public readonly struct FrameTicks
    {
        public readonly int Frame;
        public readonly int Ticks;

        public FrameTicks(int frame, int ticks)
        {
            Frame = frame;
            Ticks = ticks;
        }
    }

    // One of the slowest single calls of a frame: the thing's ID number (or 0) and its self time.
    public readonly struct Individual
    {
        public readonly int Key;
        public readonly long Id;
        public readonly long SelfTicks;

        public Individual(int key, long id, long selfTicks)
        {
            Key = key;
            Id = id;
            SelfTicks = selfTicks;
        }
    }

    // A group's figures over the selected frames. Per-tick values divide by the game ticks of those frames;
    // P95 and worst compare the per-tick value of each frame (frames without the group count as zero).
    public sealed class GroupStat
    {
        public int Key;
        public string Name;
        public double MeanMsPerTick;
        public double P95MsPerTick;
        public double WorstMsPerTick;
        public int WorstFrame;
        public double CallsPerTick;
        public int Frames;
    }
}
