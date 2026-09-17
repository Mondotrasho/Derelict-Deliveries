using System.Collections;
using UnityEngine;

public class DialoguePanelLauncher : MonoBehaviour
{
    [SerializeField] private DialoguePanelController dialoguePanel;
    [SerializeField] private TextAsset dialogueJson;
    [SerializeField] private bool runBootSequence;

    public void OpenDialogue()
    {
        if (dialoguePanel == null || dialogueJson == null)
        {
            Debug.LogWarning("DialoguePanelLauncher needs both a DialoguePanelController and dialogue JSON.", this);
            return;
        }

        dialoguePanel.OpenDialogue(dialogueJson, runBootSequence);
    }

    public void CloseDialogue()
    {
        if (dialoguePanel != null)
        {
            dialoguePanel.CloseDialogue();
        }
    }

    public void SetDialogue(TextAsset jsonFile)
    {
        dialogueJson = jsonFile;
    }

    public IEnumerator OpenDialogueAndWait()
    {
        if (dialoguePanel == null || dialogueJson == null)
        {
            yield break;
        }

        yield return dialoguePanel.OpenDialogueAndWait(dialogueJson, runBootSequence);
    }
}
