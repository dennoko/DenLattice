using System.Collections.Generic;
using UnityEngine;

namespace Dennokoworks.DenLattice
{
    /// <summary>
    /// VRChat アバター改変向けの非破壊ラティス（FFD）変形コンポーネント。
    ///
    /// <b>データモデルの要点：</b>通常のラティスツールと違い、変形の実体は
    /// 「制御点の位置」ではなく <b>頂点ごとのデルタ</b>（<see cref="MeshEdit"/>）として保持する。
    /// 制御点オフセットは編集を続けるための作業状態にすぎず、プレビューにもビルドにも
    /// 一切関与しない。この構成のおかげで、
    ///   - 格子数を変えても既にできている形が崩れない
    ///   - プレビューとビルドの適用処理を DenMeshEditor とまったく同じ「加算 1 行」に保てる
    /// という 2 点が同時に成立する。
    ///
    /// 元メッシュアセットは書き換えない。
    /// </summary>
    [AddComponentMenu("dennokoworks/Dennoko Lattice")]
    public class DenLattice : MonoBehaviour
#if DEN_LATTICE_VRCSDK
        , VRC.SDKBase.IEditorOnly
#endif
    {
        /// <summary>格子分割数の下限。2 未満では面にならない。</summary>
        public const int MinResolution = 2;

        /// <summary>
        /// 格子分割数の上限。10^3 = 1000 制御点。
        /// これ以上はハンドルの描画とピッキングが実用的でなくなる。
        /// </summary>
        public const int MaxResolution = 10;

        [Tooltip("変形対象の Renderer。複数指定できます。")]
        public List<MeshEdit> edits = new List<MeshEdit>();

        // ------------------------------------------------------------------
        // ラティスボックス（アバタールートのローカル空間）
        //
        // シーン階層に GameObject を作らず、コンポーネントのシリアライズ値として持つ。
        // 複数 Renderer を跨いで変形するため、共通の基準空間が必要になる。

        [Tooltip("ラティスボックスの中心（アバタールート空間）。")]
        public Vector3 boxPosition;

        [Tooltip("ラティスボックスの回転（アバタールート空間）。")]
        public Quaternion boxRotation = Quaternion.identity;

        [Tooltip("ラティスボックスの各辺の長さ。")]
        public Vector3 boxSize = new Vector3(0.2f, 0.2f, 0.2f);

        /// <summary>一度でもボックスを配置したか。false のうちは編集開始時に自動フィットする。</summary>
        [HideInInspector] public bool boxInitialized;

        // ------------------------------------------------------------------
        // 格子

        [SerializeField, HideInInspector] private int resU = 3;
        [SerializeField, HideInInspector] private int resV = 3;
        [SerializeField, HideInInspector] private int resW = 3;

        /// <summary>制御点オフセット（ボックスローカル空間、メートル）。密な配列。</summary>
        [SerializeField, HideInInspector] private byte[] controlOffsets;

        [Tooltip("ラティスの補間方式。")]
        public InterpolationType interpolation = InterpolationType.Trilinear;

        // ------------------------------------------------------------------
        // 設定

        [Tooltip("有効な間に行った操作のみがミラーされます。")]
        public bool mirror;

        [Tooltip("ミラーの対称軸。ラティスボックスのローカル軸で解釈します。")]
        public LatticeAxis mirrorAxis = LatticeAxis.U;

        [Tooltip("ON にすると、元の形状を保ったまま変形分をシェイプキーとして追加します。")]
        public bool bakeAsBlendShape;

        [Tooltip("追加するシェイプキーの名前。空の場合は元メッシュ名 + _lattice になります。")]
        public string blendShapeName = string.Empty;

        // ------------------------------------------------------------------

        public int ResU => resU;
        public int ResV => resV;
        public int ResW => resW;

        public int ControlCount => resU * resV * resW;

        public int ControlIndex(int i, int j, int k)
        {
            return (k * resV + j) * resU + i;
        }

        public Vector3 GetControlOffset(int index)
        {
            if (index < 0 || index >= ControlCount) return Vector3.zero;

            EnsureOffsetBuffer();
            return VectorBlob.Read(controlOffsets, index);
        }

        public void SetControlOffset(int index, Vector3 offset)
        {
            if (index < 0 || index >= ControlCount) return;

            EnsureOffsetBuffer();
            VectorBlob.Write(controlOffsets, index, offset);
        }

        /// <summary>制御点が 1 つでも動いているか。</summary>
        public bool HasControlOffsets => VectorBlob.AnyNonZero(controlOffsets);

        public void ClearControlOffsets()
        {
            controlOffsets = null;
        }

        /// <summary>
        /// 格子数を変更する。
        ///
        /// 呼び出し側（エディタ）は変更前の変位場を新しい制御点位置で評価した
        /// <paramref name="resampled"/> を渡す。これによって「格子数を変えても
        /// 制御点ケージの形が保たれ、以後の編集も同じ場の続きとして行える」。
        /// 頂点デルタには一切触らないため、<b>メッシュの形状はこの操作で変化しない</b>。
        /// </summary>
        public void SetResolution(int u, int v, int w, Vector3[] resampled)
        {
            resU = Mathf.Clamp(u, MinResolution, MaxResolution);
            resV = Mathf.Clamp(v, MinResolution, MaxResolution);
            resW = Mathf.Clamp(w, MinResolution, MaxResolution);

            controlOffsets = null;
            if (resampled == null) return;

            EnsureOffsetBuffer();

            var count = Mathf.Min(resampled.Length, ControlCount);
            for (var i = 0; i < count; i++)
            {
                VectorBlob.Write(controlOffsets, i, resampled[i]);
            }
        }

        /// <summary>
        /// 制御点 (i, j, k) の、ボックスローカル空間での格子位置（オフセットを含まない）。
        /// </summary>
        public Vector3 GetControlRestPosition(int i, int j, int k)
        {
            return new Vector3(
                (Fraction(i, resU) - 0.5f) * boxSize.x,
                (Fraction(j, resV) - 0.5f) * boxSize.y,
                (Fraction(k, resW) - 0.5f) * boxSize.z);
        }

        private static float Fraction(int index, int resolution)
        {
            return resolution <= 1 ? 0.5f : index / (float)(resolution - 1);
        }

        private void EnsureOffsetBuffer()
        {
            controlOffsets = VectorBlob.Resize(controlOffsets, ControlCount);
        }

        /// <summary>
        /// コンポーネント追加時に呼ばれる。Renderer を持つオブジェクトに付与された場合、
        /// その Renderer を最初の変形対象として自動登録する。
        /// </summary>
        private void Reset()
        {
            if (edits.Count > 0) return;

            var renderer = GetComponent<Renderer>();
            if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
            {
                edits.Add(new MeshEdit { target = renderer });
            }
        }

        public MeshEdit FindEdit(Renderer target)
        {
            foreach (var edit in edits)
            {
                if (edit != null && edit.target == target) return edit;
            }

            return null;
        }
    }
}
