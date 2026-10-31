// COPIED from AlbumWall (src/AlbumWall.Codecs/Pinned.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — arrays that never move, for code ported from C that walks them
// with pointers (Mp3Decoder). Kept here for the life of the process: the
// tables are made once, and a decoder's state lives as long as its track.

using System.Runtime.CompilerServices;

namespace Marginal.Codecs;

internal static unsafe class Pinned
{
    private static readonly List<Array> Kept = [];

    /// A copy of `values` on the pinned heap, kept for good.
    public static T* Of<T>(T[] values) where T : unmanaged
    {
        var pinned = GC.AllocateArray<T>(values.Length, pinned: true);
        values.CopyTo(pinned, 0);
        lock (Kept) Kept.Add(pinned);
        return (T*)Unsafe.AsPointer(ref pinned[0]);
    }

    /// `length` zeros on the pinned heap, alive as long as `owner` holds the
    /// array returned beside the pointer.
    public static T* New<T>(int length, out T[] owner) where T : unmanaged
    {
        owner = GC.AllocateArray<T>(length, pinned: true);
        return (T*)Unsafe.AsPointer(ref owner[0]);
    }
}
