using System.Diagnostics;
using Unity.Profiling;
#if DEN_LATTICE_DEBUG
using UnityEditor;
#endif

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// プレビュー処理の計測用マーカー。
    ///
    /// Profiler で「どの処理が Renderer 数に比例して重くなっているか」を切り分けるために置く。
    /// マーカーは Profiler が記録していないときはほぼ無コストなので、常時残しておく。
    /// </summary>
    internal static class PreviewMarkers
    {
        internal static readonly ProfilerMarker OnFrame = new ProfilerMarker("DenLattice.OnFrame");
        internal static readonly ProfilerMarker ProbeUpstream = new ProfilerMarker("DenLattice.ProbeUpstream");
        internal static readonly ProfilerMarker Rebuild = new ProfilerMarker("DenLattice.Rebuild");
        internal static readonly ProfilerMarker GatherEdits = new ProfilerMarker("DenLattice.GatherEdits");
        internal static readonly ProfilerMarker Instantiate = new ProfilerMarker("DenLattice.Instantiate");
    }

    /// <summary>
    /// プレビュー処理の回数カウンタ。<c>DEN_LATTICE_DEBUG</c> が定義されているときだけ動く。
    ///
    /// 呼び出しは <see cref="ConditionalAttribute"/> によって、シンボルが無いビルドでは
    /// 呼び出し元ごと取り除かれる。通常の利用者には一切コストがかからない。
    /// </summary>
    internal static class PreviewStats
    {
        private static int _rebuilds;
        private static int _fullReads;
        private static int _sampleProbes;
        private static int _generatedAlive;
        private static int _generatedCreated;
        private static int _nodesCreated;
        private static int _nodeRefreshes;
        private static int _nodeReused;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountRebuild() => _rebuilds++;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountFullRead() => _fullReads++;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountSampleProbe() => _sampleProbes++;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountGeneratedCreated()
        {
            _generatedCreated++;
            _generatedAlive++;
        }

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountGeneratedDestroyed() => _generatedAlive--;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountNodeCreated() => _nodesCreated++;

        [Conditional("DEN_LATTICE_DEBUG")]
        internal static void CountNodeRefresh(bool reused)
        {
            _nodeRefreshes++;
            if (reused) _nodeReused++;
        }

#if DEN_LATTICE_DEBUG
        private const string MenuRoot = "Tools/dennokoworks/Dennoko Lattice/Debug/";

        [MenuItem(MenuRoot + "Log Preview Stats")]
        private static void LogStats()
        {
            UnityEngine.Debug.Log(
                "[Dennoko Lattice] Preview stats — "
                + $"Rebuild: {_rebuilds}, FullRead: {_fullReads}, SampleProbe: {_sampleProbes}, "
                + $"Generated alive: {_generatedAlive} (created {_generatedCreated}), "
                + $"Node created: {_nodesCreated}, Node refresh: {_nodeRefreshes} (reused {_nodeReused})");
        }

        [MenuItem(MenuRoot + "Reset Preview Stats")]
        private static void ResetStats()
        {
            // 生存数は実在するメッシュの数なのでリセットしない
            _rebuilds = 0;
            _fullReads = 0;
            _sampleProbes = 0;
            _generatedCreated = 0;
            _nodesCreated = 0;
            _nodeRefreshes = 0;
            _nodeReused = 0;
        }
#endif
    }
}
