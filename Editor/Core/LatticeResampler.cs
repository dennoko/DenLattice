using UnityEditor;
using UnityEngine;

namespace Dennokoworks.DenLattice.Editor
{
    /// <summary>
    /// 格子数の変更と制御点のリセット。
    ///
    /// <b>いずれの操作もメッシュの形状を変えない。</b>変形の実体は頂点デルタとして
    /// <see cref="MeshEdit"/> に入っており、制御点オフセットは「続きを編集するための足場」
    /// でしかないため。通常のラティスツールが格子数の変更で変形を失う（あるいは事前に
    /// モディファイアの適用を要求する）のに対して、本ツールが何度でも往復できるのはこの構造による。
    ///
    /// 編集セッションの有無に関わらず呼べるよう、コンポーネントだけを見る形にしてある。
    /// </summary>
    internal static class LatticeResampler
    {
        /// <summary>
        /// 格子数を変更する。制御点はボックスの格子位置へ戻る。
        ///
        /// 旧いケージの形を新しい格子でサンプリングし直すこともできるが、そうすると
        /// 「格子を細かくしたのに、ケージが歪んだままでどこを掴めばいいのか分からない」
        /// 状態になる。オフセットはあくまで足場であり、捨てても形状は失われないので、
        /// 毎回まっさらな格子から掴み直せる方を採る。
        /// </summary>
        /// <returns>実際に変更したら true。</returns>
        internal static bool ChangeResolution(DenLattice component, int u, int v, int w)
        {
            if (component == null) return false;

            u = Mathf.Clamp(u, DenLattice.MinResolution, DenLattice.MaxResolution);
            v = Mathf.Clamp(v, DenLattice.MinResolution, DenLattice.MaxResolution);
            w = Mathf.Clamp(w, DenLattice.MinResolution, DenLattice.MaxResolution);

            if (u == component.ResU && v == component.ResV && w == component.ResW) return false;

            RecordUndo(component, "Dennoko Lattice Resolution");

            // resampled に null を渡すと、新しい格子数でオフセットが空のまま作り直される
            component.SetResolution(u, v, w, null);

            FinishUndo(component);

            return true;
        }

        /// <summary>制御点を格子位置へ戻す。確定済みの頂点デルタには触れない。</summary>
        internal static bool ResetCage(DenLattice component)
        {
            if (component == null || !component.HasControlOffsets) return false;

            RecordUndo(component, "Dennoko Lattice Reset Cage");
            component.ClearControlOffsets();
            FinishUndo(component);

            return true;
        }

        private static void RecordUndo(Object target, string name)
        {
            // Unity は同じグループ内の RecordObject を 1 段にまとめるため、
            // 明示的に切らないと無関係な操作どうしが 1 段に潰れる
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(name);
            Undo.RecordObject(target, name);
        }

        private static void FinishUndo(Object target)
        {
            EditorUtility.SetDirty(target);

            // SerializedObject を経由せずフィールドを直接書き換えているため、
            // Prefab インスタンス上でオーバーライドとして記録されるよう明示しておく
            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }

            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();
        }
    }
}
