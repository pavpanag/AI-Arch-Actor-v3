using UnityEngine;

namespace AaltoSystemV3
{

public class ChatTraceControls : MonoBehaviour
{
	public void StartNewSession() => ChatTraceLogger.StartNewSession();
	public void SaveSessionToFile() => ChatTraceLogger.SaveSessionToFile();

	public void RevealTraceFolder() => ChatTraceLogger.RevealTraceFolder();
	public void RevealLastSavedTraceFile() => ChatTraceLogger.RevealLastSavedTraceFile();
	public void RevealLatestRequestFile() => ChatTraceLogger.RevealLatestRequestFile();
	public void RevealLatestResponseFile() => ChatTraceLogger.RevealLatestResponseFile();
}
}
