using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BundleDiff
{
    public struct BlockSum
    {
        public int BlockStart;
        public int BlockSize;
        public bool RemoteBlock;
        public uint WeakSum;
        public ulong StrongSum;
    }
}