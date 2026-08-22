using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// コンポーネントの書き換えを Undo へ積むための共通処理。
    ///
    /// <b>本ツールでは <see cref="Undo.RecordObject"/> を使ってはいけない。</b>
    ///
    /// <c>RecordObject</c> は「変更前の状態を控えておき、フラッシュ時に変更後と比較して
    /// <c>PropertyModification</c>（プロパティパスと値の文字列表現の列）を作る」実装で、
    /// コストが<b>オブジェクトのシリアライズ内容の量</b>に比例する。Ctrl+Z ではその列を
    /// 逆向きに適用し直すため、往路と復路の両方で同じコストがかかる。
    ///
    /// 本ツールの変形データは 1 頂点 16 バイトの <c>byte[]</c> で、ラティスは 1 ドラッグで
    /// ボックス内の全頂点を動かす。3 万頂点なら 480KB になり、これをプロパティ差分として
    /// 往復させると Undo 1 回が数百ミリ秒の停止になる（DenMeshEditor のブラシは一度に
    /// 数百頂点しか触らないため、同じ構造でも問題にならなかった）。
    ///
    /// <see cref="Undo.RegisterCompleteObjectUndo"/> はオブジェクトのシリアライズ状態を
    /// バイナリのスナップショットとして積むだけなので、同じデータ量でも桁が違う。
    /// 差分ではなく全体を積むぶんメモリは食うが、本ツールでは 1 回のドラッグで
    /// blob 全体が書き変わる（＝差分＝全体）ため、実質的な差は無い。
    /// </summary>
    internal static class DenLatticeUndo
    {
        /// <summary>
        /// Undo グループを切って、変更前の状態を積む。書き換えの<b>前</b>に呼ぶこと。
        ///
        /// グループを切らないと、Unity は同じグループ内の記録を 1 段にまとめてしまい、
        /// 無関係な操作どうしが Ctrl+Z 一回でまとめて巻き戻る。
        /// </summary>
        internal static void BeginGroup(Object target, string name)
        {
            if (target == null) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            Undo.RegisterCompleteObjectUndo(target, name);
        }

        /// <summary>
        /// グループを切らずに、変更前の状態だけを積む。
        /// 連続した設定変更を 1 段へまとめたい場合（オーバーレイ）に使う。
        /// </summary>
        internal static void Record(Object target, string name)
        {
            if (target == null) return;

            Undo.RegisterCompleteObjectUndo(target, name);
        }

        /// <summary>
        /// 書き換えた<b>後</b>に呼ぶ。
        ///
        /// <c>RegisterCompleteObjectUndo</c> は呼んだ時点でスナップショットを積むため、
        /// <c>RecordObject</c> のようなフラッシュ（<c>Undo.FlushUndoRecordObjects</c>）は要らない。
        /// 一方で、Prefab インスタンス上のオーバーライドは差分検出に乗らなくなるので、
        /// ここで明示的に記録する。
        /// </summary>
        internal static void Apply(Object target)
        {
            if (target == null) return;

            EditorUtility.SetDirty(target);

            if (!PrefabUtility.IsPartOfPrefabInstance(target)) return;

            // 後から足したコンポーネントは「追加コンポーネントのオーバーライド」として
            // シリアライズ内容がまるごとシーンへ保存されるため、プロパティ単位の記録が要らない。
            // RecordPrefabInstancePropertyModifications は Prefab アセットとの全プロパティ照合で、
            // 変形データの量に比例して重くなる（数十万頂点で「Hold on」が出る）ので、
            // 必要な構成でだけ通す
            if (target is Component component && PrefabUtility.IsAddedComponentOverride(component)) return;

            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        /// <summary>次の操作が同じ段へ入らないようにグループを閉じる。</summary>
        internal static void EndGroup()
        {
            Undo.IncrementCurrentGroup();
        }
    }
}
