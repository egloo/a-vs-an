﻿using System;

namespace AvsAnLib {
    public struct Node : IComparable<Node> {
        public char c;
        public Ratio ratio;
        public Node[] SortedKids;
        public int CompareTo(Node other) { return c.CompareTo(other.c); }
    }
}