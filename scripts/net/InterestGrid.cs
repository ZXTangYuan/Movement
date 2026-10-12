using Godot;
using System;
using System.Collections.Generic;

namespace ProjectA.Net;

/// <summary>
/// 3D 空间兴趣网格。对应 Astroneer 的 UAstroReplicationGraphNode_GridSpatialization3D。
/// 只把“某区域附近”的地形编辑发给相关 peer，远处玩家互不干扰 —— 可扩展性的根本来源。
/// </summary>
public sealed class InterestGrid
{
    private readonly float _cellSize;
    private readonly Dictionary<long, HashSet<int>> _cellToPeers = new();
    private readonly Dictionary<int, Vector3> _peerPositions = new();
    private readonly Dictionary<int, long> _peerCell = new();

    public InterestGrid(float cellSize = 256.0f)
    {
        _cellSize = Mathf.Max(1.0f, cellSize);
    }

    public void SetPeerPosition(int peerId, Vector3 planetLocalPos)
    {
        _peerPositions[peerId] = planetLocalPos;
        long cell = CellKey(planetLocalPos);
        if (_peerCell.TryGetValue(peerId, out long old) && old == cell)
            return;

        if (_peerCell.TryGetValue(peerId, out old) && _cellToPeers.TryGetValue(old, out var set))
        {
            set.Remove(peerId);
            if (set.Count == 0) _cellToPeers.Remove(old);
        }
        _peerCell[peerId] = cell;
        if (!_cellToPeers.TryGetValue(cell, out var bucket))
        {
            bucket = new HashSet<int>();
            _cellToPeers[cell] = bucket;
        }
        bucket.Add(peerId);
    }

    public void RemovePeer(int peerId)
    {        if (_peerCell.TryGetValue(peerId, out long cell) &&
            _cellToPeers.TryGetValue(cell, out var set))
        {
            set.Remove(peerId);
            if (set.Count == 0) _cellToPeers.Remove(cell);
        }
        _peerCell.Remove(peerId);
        _peerPositions.Remove(peerId);
    }

    public void Clear()
    {
        _cellToPeers.Clear();
        _peerPositions.Clear();
        _peerCell.Clear();
    }

    /// <summary>
    /// 返回所有“对 position 感兴趣”的 peer。按半径（以格为单位）扩展搜索。
    /// </summary>
    public IReadOnlyCollection<int> GetInterestedPeers(Vector3 position, int radiusCells = 1)
    {
        var result = new HashSet<int>();
        long cx, cy, cz;
        Decompose(CellKey(position), out cx, out cy, out cz);
        for (long x = cx - radiusCells; x <= cx + radiusCells; x++)
        for (long y = cy - radiusCells; y <= cy + radiusCells; y++)
        for (long z = cz - radiusCells; z <= cz + radiusCells; z++)
        {
            long key = Compose(x, y, z);
            if (_cellToPeers.TryGetValue(key, out var set))
                result.UnionWith(set);
        }
        return result;
    }

    private long CellKey(Vector3 p) =>
        Compose(
            (long)Mathf.Floor(p.X / _cellSize),
            (long)Mathf.Floor(p.Y / _cellSize),
            (long)Mathf.Floor(p.Z / _cellSize));

    private static long Compose(long x, long y, long z) =>
        (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);

    private static void Decompose(long key, out long x, out long y, out long z)
    {
        x = key & 0x1FFFFF;
        y = (key >> 21) & 0x1FFFFF;
        z = (key >> 42) & 0x1FFFFF;
        // 符号还原（21 位有符号）
        if (x >= 0x100000) x -= 0x200000;
        if (y >= 0x100000) y -= 0x200000;
        if (z >= 0x100000) z -= 0x200000;
    }
}
