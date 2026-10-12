using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ThothTAS.Core
{
    public enum AimKind
    {
        None = 0,
        Vector = 1,
        WorldTarget = 2
    }

    /// <summary>One immutable script input. World targets are resolved at the current firing position.</summary>
    public sealed class TasFrameInput
    {
        public double MoveX { get; private set; }
        public double MoveY { get; private set; }
        public AimKind AimKind { get; private set; }
        public double AimX { get; private set; }
        public double AimY { get; private set; }

        public TasFrameInput(double moveX, double moveY, AimKind aimKind, double aimX, double aimY)
        {
            MoveX = moveX;
            MoveY = moveY;
            AimKind = aimKind;
            AimX = aimX;
            AimY = aimY;
        }

        public void ResolveAim(double firingX, double firingY, out double x, out double y)
        {
            if (double.IsNaN(firingX) || double.IsInfinity(firingX)) throw new ArgumentOutOfRangeException("firingX");
            if (double.IsNaN(firingY) || double.IsInfinity(firingY)) throw new ArgumentOutOfRangeException("firingY");
            if (AimKind == AimKind.None)
            {
                x = 0;
                y = 0;
                return;
            }
            if (AimKind == AimKind.Vector)
            {
                x = AimX;
                y = AimY;
                return;
            }
            x = AimX - firingX;
            y = AimY - firingY;
            if (double.IsInfinity(x) || double.IsInfinity(y))
            {
                double scale = Math.Max(Math.Max(Math.Abs(AimX), Math.Abs(AimY)), Math.Max(Math.Abs(firingX), Math.Abs(firingY)));
                x = AimX / scale - firingX / scale;
                y = AimY / scale - firingY / scale;
            }
            Normalize(x, y, true, out x, out y);
        }

        // Scale first: x*x + y*y may overflow for otherwise valid finite coordinates.
        internal static void Normalize(double x, double y, bool forceUnit, out double nx, out double ny)
        {
            double scale = Math.Max(Math.Abs(x), Math.Abs(y));
            if (scale == 0)
            {
                nx = 0;
                ny = 0;
                return;
            }
            double sx = x / scale;
            double sy = y / scale;
            double scaledLength = Math.Sqrt(sx * sx + sy * sy);
            if (forceUnit || scale > 1 || scale * scaledLength > 1)
            {
                nx = sx / scaledLength;
                ny = sy / scaledLength;
            }
            else
            {
                nx = x;
                ny = y;
            }
        }
    }

    public sealed class TasSegment
    {
        public int Frames { get; private set; }
        public int StartFrame { get; private set; }
        public int SourceLine { get; private set; }
        public TasFrameInput Input { get; private set; }

        internal TasSegment(int frames, int startFrame, int sourceLine, TasFrameInput input)
        {
            Frames = frames;
            StartFrame = startFrame;
            SourceLine = sourceLine;
            Input = input;
        }
    }

    /// <summary>A scene's immutable input budget; segment frame indices are local to this chapter.</summary>
    public sealed class TasChapter
    {
        public int Index { get; private set; }
        public string Scene { get; private set; }
        public int SourceLine { get; private set; }
        public string SourceOrigin { get; private set; }
        public int TotalFrames { get; private set; }
        public ReadOnlyCollection<TasSegment> Segments { get; private set; }

        internal TasChapter(int index, string scene, int sourceLine, string sourceOrigin, int totalFrames, List<TasSegment> segments)
        {
            Index = index;
            Scene = scene;
            SourceLine = sourceLine;
            SourceOrigin = sourceOrigin;
            TotalFrames = totalFrames;
            Segments = new List<TasSegment>(segments).AsReadOnly();
        }

        /// <summary>Local frames are zero based, and exclude loading and transition frames.</summary>
        public TasFrameInput GetInputAtFrame(int localFrame, out int sourceLine)
        {
            return TasScript.FindInput(Segments, TotalFrames, localFrame, "localFrame", out sourceLine);
        }
    }

    public sealed class TasScript
    {
        public int Version { get { return 1; } }
        public string Start { get; private set; }
        public int Seed { get; private set; }
        public int TotalFrames { get; private set; }
        public ReadOnlyCollection<TasSegment> Segments { get; private set; }
        public ReadOnlyCollection<TasChapter> Chapters { get; private set; }
        public bool HasExplicitRooms { get; private set; }

        internal TasScript(string start, int seed, int totalFrames, List<TasSegment> segments, List<TasChapter> chapters, bool hasExplicitRooms)
        {
            Start = start;
            Seed = seed;
            TotalFrames = totalFrames;
            Segments = new List<TasSegment>(segments).AsReadOnly();
            Chapters = new List<TasChapter>(chapters).AsReadOnly();
            HasExplicitRooms = hasExplicitRooms;
        }

        /// <summary>Zero-based nominal input-budget lookup. Explicit chapters can finish early, so this is not a runtime timeline.</summary>
        public TasFrameInput GetInputAtFrame(int frame, out int sourceLine)
        {
            return FindInput(Segments, TotalFrames, frame, "frame", out sourceLine);
        }

        public TasFrameInput GetInputAtChapterFrame(int chapterIndex, int localFrame, out int sourceLine)
        {
            if (chapterIndex < 0 || chapterIndex >= Chapters.Count)
                throw new ArgumentOutOfRangeException("chapterIndex");
            return Chapters[chapterIndex].GetInputAtFrame(localFrame, out sourceLine);
        }

        internal static TasFrameInput FindInput(ReadOnlyCollection<TasSegment> segments, int totalFrames, int frame, string parameterName, out int sourceLine)
        {
            if (frame < 0 || frame >= totalFrames)
                throw new ArgumentOutOfRangeException(parameterName);
            int low = 0;
            int high = segments.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                TasSegment segment = segments[middle];
                if (frame < segment.StartFrame)
                    high = middle - 1;
                else if (frame - segment.StartFrame >= segment.Frames)
                    low = middle + 1;
                else
                {
                    sourceLine = segment.SourceLine;
                    return segment.Input;
                }
            }
            throw new InvalidOperationException("Script frame index is inconsistent.");
        }
    }

    public sealed class ScriptParseException : FormatException
    {
        public int Line { get; private set; }
        public int Column { get; private set; }
        public string Reason { get; private set; }

        internal ScriptParseException(int line, int column, string reason)
            : base("第 " + line + " 行，第 " + column + " 列：" + reason)
        {
            Line = line;
            Column = column;
            Reason = reason;
        }
    }
}
