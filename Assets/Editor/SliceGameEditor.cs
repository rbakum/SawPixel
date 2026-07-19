using UnityEditor;
using UnityEngine;

// Inspector add-on for SliceGame: lets you type a seed and start a level with
// exactly that seed (in Play mode), or roll a fresh random one.
[CustomEditor(typeof(SliceGame))]
public class SliceGameEditor : Editor
{
    int seedInput;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var game = (SliceGame)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Seed control", EditorStyles.boldLabel);

        seedInput = EditorGUILayout.IntField("Seed to start", seedInput);

        using (new EditorGUI.DisabledScope(!Application.isPlaying))
        {
            if (GUILayout.Button("Start level with this seed"))
                game.Restart(seedInput);
            if (GUILayout.Button("Restart with random seed"))
                game.Restart();
        }

        if (Application.isPlaying)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Current seed", game.CurrentSeed.ToString());
            if (GUILayout.Button("Copy current seed to input"))
                seedInput = game.CurrentSeed;
        }
        else
        {
            EditorGUILayout.HelpBox("Enter Play mode to start a level with a specific seed.", MessageType.Info);
        }
    }
}
