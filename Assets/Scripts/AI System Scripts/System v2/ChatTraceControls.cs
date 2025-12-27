using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class ChatTraceControls : MonoBehaviour
{
	public void StartNewSession() => ChatTraceLogger.StartNewSession();
	public void SaveSessionToFile() => ChatTraceLogger.SaveSessionToFile();

	public void RevealTraceFolder()
	{
		var path = System.IO.Path.Combine(Application.persistentDataPath, "chat_traces");
		Debug.Log($"[ChatTraceControls] Traces folder: {path}");
		#if UNITY_EDITOR
		EditorUtility.RevealInFinder(path);
		#endif
	}
}
