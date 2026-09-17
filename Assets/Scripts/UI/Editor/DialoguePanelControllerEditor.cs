using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(DialoguePanelController))]
public class DialoguePanelControllerEditor : Editor
{
    private const string FontFolder = "Assets/Fonts";
    private const string DialogueFolder = "Assets/Narrative/Dialogue";

    private readonly List<TMP_FontAsset> fonts = new List<TMP_FontAsset>();
    private readonly List<TextAsset> dialogueFiles = new List<TextAsset>();
    private readonly List<string> dialogueNames = new List<string>();

    private SerializedProperty dialogueJson;
    private SerializedProperty characterDefinitions;
    private SerializedProperty hidePortraitWhenNoSprite;

    private SerializedProperty sideAreaFraction;
    private SerializedProperty controlsHeightFraction;
    private SerializedProperty outerInsetFraction;
    private SerializedProperty portraitPaddingFraction;
    private SerializedProperty topInsetFraction;

    private SerializedProperty panelBackgroundSprite;
    private SerializedProperty useSlicedPanelBackground;
    private SerializedProperty tintPanelBackgroundWithTerminalColour;
    private SerializedProperty panelBackgroundTint;

    private SerializedProperty defaultScanlines;
    private SerializedProperty scanlineThickness;
    private SerializedProperty scanlineGap;
    private SerializedProperty scanlineOpacity;

    private SerializedProperty playPortraitAppearEffect;
    private SerializedProperty portraitAppearDuration;
    private SerializedProperty portraitAppearJitter;
    private SerializedProperty animateSpeakingPortrait;
    private SerializedProperty speakingScaleAmount;
    private SerializedProperty speakingPulseSpeed;
    private SerializedProperty instantSpeechPulseDuration;

    private SerializedProperty colourPreset;
    private SerializedProperty customBackgroundColour;
    private SerializedProperty customPrimaryColour;
    private SerializedProperty customDimColour;

    private SerializedProperty defaultTitle;
    private SerializedProperty defaultStatus;

    private SerializedProperty fallbackSystemFont;
    private SerializedProperty fallbackCharacterFont;
    private SerializedProperty fallbackSystemFontSize;
    private SerializedProperty fallbackCharacterFontSize;
    private SerializedProperty bootFontSize;
    private SerializedProperty buttonFontSize;

    private SerializedProperty fontTestBaseSize;
    private SerializedProperty fontTestSample;

    private SerializedProperty typewriterEffect;
    private SerializedProperty defaultCharactersPerSecond;
    private SerializedProperty defaultLineInterval;
    private SerializedProperty automaticMode;

    private SerializedProperty showDebugResetButton;
    private SerializedProperty continueButtonText;
    private SerializedProperty choiceAreaFraction;
    private SerializedProperty showCloseButton;
    private SerializedProperty closeButtonText;
    private SerializedProperty bringToFrontOnOpen;

    private SerializedProperty animateWindowOpen;
    private SerializedProperty windowOpenDuration;
    private SerializedProperty windowOpenStartScale;
    private SerializedProperty windowOpenOvershoot;
    private SerializedProperty fadeWindowOnOpen;

    private SerializedProperty runBootOnStart;
    private SerializedProperty keepBootLogAfterBoot;
    private SerializedProperty hideCharacterImagesDuringBoot;
    private SerializedProperty bootLines;
    private SerializedProperty defaultBootCharactersPerSecond;
    private SerializedProperty defaultConnectDuration;
    private SerializedProperty defaultReadyHold;
    private SerializedProperty defaultBlankLinesBeforeDialogue;

    private SerializedProperty startEmpty;
    private SerializedProperty autoScrollToNewest;

    private SerializedProperty fontLibrary;
    private SerializedProperty fontSizeModifiers;
    private SerializedProperty legacyCharacterSprites;

    private void OnEnable()
    {
        dialogueJson = Find("dialogueJson");
        characterDefinitions = Find("characterDefinitions");
        hidePortraitWhenNoSprite = Find("hidePortraitWhenNoSprite");

        sideAreaFraction = Find("sideAreaFraction");
        controlsHeightFraction = Find("controlsHeightFraction");
        outerInsetFraction = Find("outerInsetFraction");
        portraitPaddingFraction = Find("portraitPaddingFraction");
        topInsetFraction = Find("topInsetFraction");

        panelBackgroundSprite = Find("panelBackgroundSprite");
        useSlicedPanelBackground = Find("useSlicedPanelBackground");
        tintPanelBackgroundWithTerminalColour = Find("tintPanelBackgroundWithTerminalColour");
        panelBackgroundTint = Find("panelBackgroundTint");

        defaultScanlines = Find("defaultScanlines");
        scanlineThickness = Find("scanlineThickness");
        scanlineGap = Find("scanlineGap");
        scanlineOpacity = Find("scanlineOpacity");

        playPortraitAppearEffect = Find("playPortraitAppearEffect");
        portraitAppearDuration = Find("portraitAppearDuration");
        portraitAppearJitter = Find("portraitAppearJitter");
        animateSpeakingPortrait = Find("animateSpeakingPortrait");
        speakingScaleAmount = Find("speakingScaleAmount");
        speakingPulseSpeed = Find("speakingPulseSpeed");
        instantSpeechPulseDuration = Find("instantSpeechPulseDuration");

        colourPreset = Find("colourPreset");
        customBackgroundColour = Find("customBackgroundColour");
        customPrimaryColour = Find("customPrimaryColour");
        customDimColour = Find("customDimColour");

        defaultTitle = Find("defaultTitle");
        defaultStatus = Find("defaultStatus");

        fallbackSystemFont = Find("fallbackSystemFont");
        fallbackCharacterFont = Find("fallbackCharacterFont");
        fallbackSystemFontSize = Find("fallbackSystemFontSize");
        fallbackCharacterFontSize = Find("fallbackCharacterFontSize");
        bootFontSize = Find("bootFontSize");
        buttonFontSize = Find("buttonFontSize");

        fontTestBaseSize = Find("fontTestBaseSize");
        fontTestSample = Find("fontTestSample");

        typewriterEffect = Find("typewriterEffect");
        defaultCharactersPerSecond = Find("defaultCharactersPerSecond");
        defaultLineInterval = Find("defaultLineInterval");
        automaticMode = Find("automaticMode");

        showDebugResetButton = Find("showDebugResetButton");
        continueButtonText = Find("continueButtonText");
        choiceAreaFraction = Find("choiceAreaFraction");
        showCloseButton = Find("showCloseButton");
        closeButtonText = Find("closeButtonText");
        bringToFrontOnOpen = Find("bringToFrontOnOpen");

        animateWindowOpen = Find("animateWindowOpen");
        windowOpenDuration = Find("windowOpenDuration");
        windowOpenStartScale = Find("windowOpenStartScale");
        windowOpenOvershoot = Find("windowOpenOvershoot");
        fadeWindowOnOpen = Find("fadeWindowOnOpen");

        runBootOnStart = Find("runBootOnStart");
        keepBootLogAfterBoot = Find("keepBootLogAfterBoot");
        hideCharacterImagesDuringBoot = Find("hideCharacterImagesDuringBoot");
        bootLines = Find("bootLines");
        defaultBootCharactersPerSecond = Find("defaultBootCharactersPerSecond");
        defaultConnectDuration = Find("defaultConnectDuration");
        defaultReadyHold = Find("defaultReadyHold");
        defaultBlankLinesBeforeDialogue = Find("defaultBlankLinesBeforeDialogue");

        startEmpty = Find("startEmpty");
        autoScrollToNewest = Find("autoScrollToNewest");

        fontLibrary = Find("fontLibrary");
        fontSizeModifiers = Find("fontSizeModifiers");
        legacyCharacterSprites = Find("characterSprites");

        RefreshAssets();
        EditorApplication.projectChanged += RefreshAssets;
    }

    private void OnDisable()
    {
        EditorApplication.projectChanged -= RefreshAssets;
    }

    private SerializedProperty Find(string propertyName)
    {
        return serializedObject.FindProperty(propertyName);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        DrawDialogueDropdown();

        Header("Character Definitions");
        EditorGUILayout.HelpBox(
            "Dialogue JSON chooses the left and right character IDs. Portrait, font, alignment, flip behaviour and default text colour live here on the prefab. JSON can override a character colour for an individual conversation.",
            MessageType.Info);

        DrawCharacterDefinitions();
        EditorGUILayout.PropertyField(hidePortraitWhenNoSprite);
        DrawLegacySpriteMigration();

        Header("Generated Layout");
        EditorGUILayout.PropertyField(sideAreaFraction, new GUIContent("Side Area", "Width owned by each portrait side. 0.20 gives 20% left, 60% centre, 20% right."));
        EditorGUILayout.PropertyField(controlsHeightFraction, new GUIContent("Controls Height", "Bottom fraction reserved for the choice area plus RESET/CONTINUE/AUTO."));
        EditorGUILayout.PropertyField(outerInsetFraction);
        EditorGUILayout.PropertyField(portraitPaddingFraction);
        EditorGUILayout.PropertyField(topInsetFraction, new GUIContent("Top Window Space", "Extra responsive space above the portrait and terminal areas."));

        Header("Panel Background");
        EditorGUILayout.PropertyField(panelBackgroundSprite, new GUIContent("Background Sprite", "Assign your sliced panel/window sprite here."));
        EditorGUILayout.PropertyField(useSlicedPanelBackground, new GUIContent("Use 9-Sliced Image"));
        EditorGUILayout.PropertyField(panelBackgroundTint);
        EditorGUILayout.PropertyField(tintPanelBackgroundWithTerminalColour, new GUIContent("Multiply By Terminal Colour"));
        EditorGUILayout.HelpBox(
            "For a sliced window image, set the sprite borders in Unity's Sprite Editor, then leave Use 9-Sliced Image enabled. The background stretches with the dialogue panel without stretching the border corners.",
            MessageType.None);

        Header("Scanlines");
        EditorGUILayout.PropertyField(defaultScanlines);
        EditorGUILayout.PropertyField(scanlineThickness);
        EditorGUILayout.PropertyField(scanlineGap);
        EditorGUILayout.PropertyField(scanlineOpacity);
        EditorGUILayout.HelpBox(
            "Scanlines are rendered after the terminal text, so their dark bands pass over the glyphs rather than sitting behind them.",
            MessageType.None);

        Header("Portrait Effects");
        EditorGUILayout.PropertyField(playPortraitAppearEffect);
        if (playPortraitAppearEffect.boolValue)
        {
            EditorGUILayout.PropertyField(portraitAppearDuration);
            EditorGUILayout.PropertyField(portraitAppearJitter);
        }

        EditorGUILayout.PropertyField(animateSpeakingPortrait);
        if (animateSpeakingPortrait.boolValue)
        {
            EditorGUILayout.PropertyField(speakingScaleAmount);
            EditorGUILayout.PropertyField(speakingPulseSpeed);
            EditorGUILayout.PropertyField(instantSpeechPulseDuration);
        }

        Header("Terminal Colours");
        EditorGUILayout.PropertyField(colourPreset);
        if ((DialoguePanelController.TerminalColourPreset)colourPreset.enumValueIndex ==
            DialoguePanelController.TerminalColourPreset.Custom)
        {
            EditorGUILayout.PropertyField(customBackgroundColour);
            EditorGUILayout.PropertyField(customPrimaryColour);
            EditorGUILayout.PropertyField(customDimColour);
        }

        Header("Default Terminal Text");
        EditorGUILayout.PropertyField(defaultTitle);
        EditorGUILayout.PropertyField(defaultStatus);

        Header("Fallback Fonts");
        DrawFontAssetDropdown("System Font", fallbackSystemFont);
        DrawFontAssetDropdown("Character Font", fallbackCharacterFont);
        EditorGUILayout.PropertyField(fallbackSystemFontSize);
        EditorGUILayout.PropertyField(fallbackCharacterFontSize);
        EditorGUILayout.PropertyField(bootFontSize, new GUIContent("Boot Font Size", "Base size for boot, handshake and connection text before the selected font's modifier is applied."));
        EditorGUILayout.PropertyField(buttonFontSize, new GUIContent("Button Font Size", "Base size for RESET, CONTINUE, AUTO and generated choice buttons before the selected font's modifier is applied."));

        Header("Per-Font Size Modifiers");
        EditorGUILayout.HelpBox(
            "These are additive calibration values, not replacement sizes. Example: a requested size of 28 with a -3 modifier renders at 25. Use the font test to make different TMP fonts look visually similar.",
            MessageType.Info);
        DrawFontSizeModifiers();

        Header("Font Test");
        EditorGUILayout.PropertyField(fontTestBaseSize);
        EditorGUILayout.PropertyField(fontTestSample);

        Header("Typing");
        EditorGUILayout.PropertyField(typewriterEffect);
        EditorGUILayout.PropertyField(defaultCharactersPerSecond);
        EditorGUILayout.PropertyField(defaultLineInterval);
        EditorGUILayout.PropertyField(automaticMode);

        Header("Dialogue Controls");
        EditorGUILayout.PropertyField(showDebugResetButton, new GUIContent("Debug Reset Button", "Shows RESET in the fixed left slot. Leave this off for normal player-facing dialogue."));
        EditorGUILayout.PropertyField(continueButtonText, new GUIContent("Continue Button Text"));
        EditorGUILayout.PropertyField(choiceAreaFraction, new GUIContent("Choice Area Height", "Fraction of the bottom controls area reserved for generated choice buttons above CONTINUE."));
        EditorGUILayout.PropertyField(showCloseButton, new GUIContent("Show Quit Button", "Shows the fixed QUIT/CLOSE button. Useful while testing gameplay behind the dialogue panel."));
        if (showCloseButton.boolValue)
        {
            EditorGUILayout.PropertyField(closeButtonText, new GUIContent("Quit Button Text"));
        }
        EditorGUILayout.PropertyField(bringToFrontOnOpen, new GUIContent("Bring To Front On Open", "Moves the existing panel to the end of its Canvas sibling list without changing its RectTransform position."));
        EditorGUILayout.HelpBox(
            "RESET is optional on the left. CONTINUE fills the centre and responds to Space. AUTO and QUIT stay in fixed slots on the right. Choices still occupy the area above the bottom bar.",
            MessageType.None);

        Header("Window Open Animation");
        EditorGUILayout.PropertyField(animateWindowOpen, new GUIContent("Animate Window Open"));
        if (animateWindowOpen.boolValue)
        {
            EditorGUILayout.PropertyField(windowOpenDuration, new GUIContent("Open Duration"));
            EditorGUILayout.PropertyField(
                windowOpenStartScale,
                new GUIContent(
                    "Start Scale",
                    "Relative to the panel's normal authored scale. Default 0.94 x 0.06 gives a CRT/terminal-style vertical reveal."));
            EditorGUILayout.PropertyField(
                windowOpenOvershoot,
                new GUIContent("Open Overshoot", "Small terminal-style pop before settling."));
            EditorGUILayout.PropertyField(fadeWindowOnOpen, new GUIContent("Fade While Opening"));
        }
        EditorGUILayout.HelpBox(
            "The animation only changes localScale and CanvasGroup alpha. It does not change the panel's anchors, anchored position or configured size.",
            MessageType.None);

        Header("Boot");
        EditorGUILayout.PropertyField(runBootOnStart);
        EditorGUILayout.PropertyField(keepBootLogAfterBoot);
        EditorGUILayout.PropertyField(hideCharacterImagesDuringBoot);
        EditorGUILayout.PropertyField(bootLines, true);
        EditorGUILayout.PropertyField(defaultBootCharactersPerSecond);
        EditorGUILayout.PropertyField(defaultConnectDuration);
        EditorGUILayout.PropertyField(defaultReadyHold);
        EditorGUILayout.PropertyField(defaultBlankLinesBeforeDialogue);

        Header("Behaviour");
        EditorGUILayout.PropertyField(startEmpty);
        EditorGUILayout.PropertyField(autoScrollToNewest);
        EditorGUILayout.HelpBox(
            "Choice JSON is optional. Existing dialogue JSON without a choices array continues to use the normal sequential tick flow. The whole dialogue GameObject may start inactive; another active script can call OpenDialogue(json) to activate and reuse it at its existing position.",
            MessageType.Info);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField(
            $"{fonts.Count} TMP font assets found under {FontFolder}",
            EditorStyles.miniLabel);

        if (GUILayout.Button("Refresh Asset Lists"))
        {
            RefreshAssets();
        }

        if (Application.isPlaying)
        {
            Header("Runtime Test");
            DialoguePanelController controller = (DialoguePanelController)target;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Next")) controller.NextTick();
            if (GUILayout.Button("Reset")) controller.ResetDialogue();
            if (GUILayout.Button("Close")) controller.CloseDialogue();
            EditorGUILayout.EndHorizontal();

            TextAsset selectedDialogue = dialogueJson.objectReferenceValue as TextAsset;
            if (selectedDialogue != null && GUILayout.Button("Open Selected Dialogue"))
            {
                controller.OpenDialogue(selectedDialogue);
            }

            if (GUILayout.Button("Replay Window Open Animation"))
            {
                controller.ReplayWindowOpenAnimation();
            }

            if (GUILayout.Button("Test All Fonts In Terminal")) controller.ShowFontTest();
            if (GUILayout.Button("Run Boot Sequence")) controller.RunBootSequence();
        }
        else
        {
            Header("Runtime Test");
            EditorGUILayout.HelpBox(
                "Enter Play Mode to use Test All Fonts. The test writes one terminal line per TMP font showing the common base size, that font's +/- modifier and the final rendered size.",
                MessageType.None);
        }

        serializedObject.ApplyModifiedProperties();
    }

    private void Header(string title)
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
    }

    private void DrawCharacterDefinitions()
    {
        if (characterDefinitions == null)
        {
            return;
        }

        int removeIndex = -1;

        for (int i = 0; i < characterDefinitions.arraySize; i++)
        {
            SerializedProperty element = characterDefinitions.GetArrayElementAtIndex(i);
            SerializedProperty characterId = element.FindPropertyRelative("characterId");
            SerializedProperty displayName = element.FindPropertyRelative("displayName");
            SerializedProperty sprite = element.FindPropertyRelative("sprite");
            SerializedProperty fontName = element.FindPropertyRelative("fontName");
            SerializedProperty fontSize = element.FindPropertyRelative("fontSize");
            SerializedProperty verticalAlignment = element.FindPropertyRelative("verticalAlignment");
            SerializedProperty flipWhenLeft = element.FindPropertyRelative("flipWhenLeft");
            SerializedProperty flipWhenRight = element.FindPropertyRelative("flipWhenRight");
            SerializedProperty portraitScale = element.FindPropertyRelative("portraitScale");
            SerializedProperty overrideTextColour = element.FindPropertyRelative("overrideTextColour");
            SerializedProperty textColour = element.FindPropertyRelative("textColour");

            string heading = string.IsNullOrWhiteSpace(characterId.stringValue)
                ? $"Character {i + 1}"
                : characterId.stringValue;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);
            if (GUILayout.Button("Remove", GUILayout.Width(70f)))
            {
                removeIndex = i;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(characterId, new GUIContent("ID"));
            EditorGUILayout.PropertyField(displayName);
            EditorGUILayout.PropertyField(sprite);
            DrawFontNameDropdown("Dialogue Font", fontName);
            EditorGUILayout.PropertyField(fontSize, new GUIContent("Default Font Size", "The selected TMP font modifier is added to this requested size."));
            EditorGUILayout.PropertyField(verticalAlignment, new GUIContent("Portrait Anchor"));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(flipWhenLeft);
            EditorGUILayout.PropertyField(flipWhenRight);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(portraitScale);
            EditorGUILayout.PropertyField(overrideTextColour, new GUIContent("Use Character Text Colour", "When enabled this is the character's default dialogue colour. JSON can still override it for an individual conversation."));
            EditorGUILayout.PropertyField(textColour, new GUIContent("Text Colour", "Always editable here. It is used when Use Character Text Colour is enabled and no JSON colour override exists."));

            EditorGUILayout.EndVertical();
        }

        if (removeIndex >= 0)
        {
            characterDefinitions.DeleteArrayElementAtIndex(removeIndex);
        }

        if (GUILayout.Button("Add Character Definition"))
        {
            int index = characterDefinitions.arraySize;
            characterDefinitions.arraySize++;
            SerializedProperty element = characterDefinitions.GetArrayElementAtIndex(index);
            ResetCharacterDefinition(element);
        }
    }

    private void ResetCharacterDefinition(SerializedProperty element)
    {
        element.FindPropertyRelative("characterId").stringValue = "";
        element.FindPropertyRelative("displayName").stringValue = "";
        element.FindPropertyRelative("sprite").objectReferenceValue = null;
        element.FindPropertyRelative("fontName").stringValue = "";
        element.FindPropertyRelative("fontSize").floatValue = 0f;
        element.FindPropertyRelative("verticalAlignment").enumValueIndex =
            (int)DialoguePanelController.PortraitVerticalAlignment.Bottom;
        element.FindPropertyRelative("flipWhenLeft").boolValue = false;
        element.FindPropertyRelative("flipWhenRight").boolValue = false;
        element.FindPropertyRelative("portraitScale").floatValue = 1f;
        element.FindPropertyRelative("overrideTextColour").boolValue = false;
        element.FindPropertyRelative("textColour").colorValue = Color.white;
    }

    private void DrawLegacySpriteMigration()
    {
        if (legacyCharacterSprites == null || legacyCharacterSprites.arraySize == 0)
        {
            return;
        }

        int assignedCount = 0;
        for (int i = 0; i < legacyCharacterSprites.arraySize; i++)
        {
            SerializedProperty element = legacyCharacterSprites.GetArrayElementAtIndex(i);
            if (element.FindPropertyRelative("sprite").objectReferenceValue != null)
            {
                assignedCount++;
            }
        }

        if (assignedCount == 0)
        {
            return;
        }

        EditorGUILayout.HelpBox(
            $"Found {assignedCount} portrait assignment(s) from the previous sprite map. The runtime already uses them as a fallback.",
            MessageType.Info);

        if (GUILayout.Button("Copy Legacy Sprites Into Character Definitions"))
        {
            CopyLegacySpritesIntoDefinitions();
        }
    }

    private void CopyLegacySpritesIntoDefinitions()
    {
        serializedObject.Update();

        for (int i = 0; i < legacyCharacterSprites.arraySize; i++)
        {
            SerializedProperty legacy = legacyCharacterSprites.GetArrayElementAtIndex(i);
            string id = legacy.FindPropertyRelative("characterId").stringValue;
            UnityEngine.Object sprite = legacy.FindPropertyRelative("sprite").objectReferenceValue;

            if (string.IsNullOrWhiteSpace(id) || sprite == null)
            {
                continue;
            }

            SerializedProperty definition = FindCharacterDefinitionProperty(id);
            if (definition == null)
            {
                int newIndex = characterDefinitions.arraySize;
                characterDefinitions.arraySize++;
                definition = characterDefinitions.GetArrayElementAtIndex(newIndex);
                ResetCharacterDefinition(definition);
                definition.FindPropertyRelative("characterId").stringValue = id;
                definition.FindPropertyRelative("displayName").stringValue = id.ToUpperInvariant();
            }

            SerializedProperty definitionSprite = definition.FindPropertyRelative("sprite");
            if (definitionSprite.objectReferenceValue == null)
            {
                definitionSprite.objectReferenceValue = sprite;
            }
        }

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
    }

    private SerializedProperty FindCharacterDefinitionProperty(string id)
    {
        for (int i = 0; i < characterDefinitions.arraySize; i++)
        {
            SerializedProperty definition = characterDefinitions.GetArrayElementAtIndex(i);
            string existingId = definition.FindPropertyRelative("characterId").stringValue;

            if (string.Equals(existingId, id, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private void DrawFontNameDropdown(string label, SerializedProperty fontNameProperty)
    {
        List<string> names = new List<string> { "None / Fallback" };
        List<string> values = new List<string> { "" };

        string current = fontNameProperty.stringValue ?? "";
        bool currentExists = string.IsNullOrWhiteSpace(current);

        foreach (TMP_FontAsset font in fonts)
        {
            string cleanName = CleanFontName(font.name);
            names.Add(cleanName);
            values.Add(cleanName);

            if (string.Equals(NormaliseFontName(cleanName), NormaliseFontName(current), StringComparison.Ordinal))
            {
                currentExists = true;
            }
        }

        if (!currentExists)
        {
            names.Add(current + " (missing)");
            values.Add(current);
        }

        int currentIndex = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(NormaliseFontName(values[i]), NormaliseFontName(current), StringComparison.Ordinal))
            {
                currentIndex = i;
                break;
            }
        }

        int newIndex = EditorGUILayout.Popup(label, currentIndex, names.ToArray());
        if (newIndex != currentIndex)
        {
            fontNameProperty.stringValue = values[newIndex];
        }
    }

    private void DrawFontSizeModifiers()
    {
        if (fontSizeModifiers == null || fontSizeModifiers.arraySize == 0)
        {
            EditorGUILayout.LabelField("No TMP fonts found.", EditorStyles.miniLabel);
            return;
        }

        for (int i = 0; i < fontSizeModifiers.arraySize; i++)
        {
            SerializedProperty entry = fontSizeModifiers.GetArrayElementAtIndex(i);
            SerializedProperty font = entry.FindPropertyRelative("font");
            SerializedProperty modifier = entry.FindPropertyRelative("modifier");

            TMP_FontAsset asset = font.objectReferenceValue as TMP_FontAsset;
            string label = asset != null ? CleanFontName(asset.name) : "Missing Font";
            EditorGUILayout.PropertyField(modifier, new GUIContent(label, "Added to every requested size that uses this TMP font. Negative values make the font smaller; positive values make it larger."));
        }
    }

    private void RefreshAssets()
    {
        if (serializedObject == null)
        {
            return;
        }

        RefreshDialogueFiles();
        RefreshFonts();
        Repaint();
    }

    private void RefreshDialogueFiles()
    {
        dialogueFiles.Clear();
        dialogueNames.Clear();

        if (!AssetDatabase.IsValidFolder(DialogueFolder))
        {
            return;
        }

        string[] guids = AssetDatabase.FindAssets("t:TextAsset", new[] { DialogueFolder });
        List<string> paths = new List<string>();

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        paths.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        foreach (string path in paths)
        {
            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset == null)
            {
                continue;
            }

            dialogueFiles.Add(asset);

            string displayName = path.StartsWith(DialogueFolder + "/", StringComparison.OrdinalIgnoreCase)
                ? path.Substring(DialogueFolder.Length + 1)
                : Path.GetFileName(path);

            dialogueNames.Add(Path.ChangeExtension(displayName, null));
        }
    }

    private void DrawDialogueDropdown()
    {
        TextAsset current = dialogueJson.objectReferenceValue as TextAsset;

        List<TextAsset> choices = new List<TextAsset> { null };
        List<string> names = new List<string> { "None" };

        if (current != null && !dialogueFiles.Contains(current))
        {
            choices.Add(current);
            names.Add(current.name + " (outside Narrative/Dialogue)");
        }

        for (int i = 0; i < dialogueFiles.Count; i++)
        {
            choices.Add(dialogueFiles[i]);
            names.Add(dialogueNames[i]);
        }

        int currentIndex = choices.IndexOf(current);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int newIndex = EditorGUILayout.Popup("Dialogue JSON", currentIndex, names.ToArray());
        if (newIndex != currentIndex)
        {
            dialogueJson.objectReferenceValue = choices[newIndex];
        }

        if (!AssetDatabase.IsValidFolder(DialogueFolder))
        {
            EditorGUILayout.HelpBox($"Dialogue folder not found: {DialogueFolder}", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.LabelField(
                $"{dialogueFiles.Count} JSON files found under {DialogueFolder}",
                EditorStyles.miniLabel);
        }
    }

    private void RefreshFonts()
    {
        fonts.Clear();

        if (AssetDatabase.IsValidFolder(FontFolder))
        {
            string[] guids = AssetDatabase.FindAssets("t:TMP_FontAsset", new[] { FontFolder });

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (font != null)
                {
                    fonts.Add(font);
                }
            }
        }

        fonts.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        SyncFontData();
    }

    private void SyncFontData()
    {
        serializedObject.Update();

        Dictionary<TMP_FontAsset, float> existingModifiers = new Dictionary<TMP_FontAsset, float>();
        if (fontSizeModifiers != null)
        {
            for (int i = 0; i < fontSizeModifiers.arraySize; i++)
            {
                SerializedProperty entry = fontSizeModifiers.GetArrayElementAtIndex(i);
                TMP_FontAsset font = entry.FindPropertyRelative("font").objectReferenceValue as TMP_FontAsset;
                float modifier = entry.FindPropertyRelative("modifier").floatValue;

                if (font != null && !existingModifiers.ContainsKey(font))
                {
                    existingModifiers.Add(font, modifier);
                }
            }
        }

        fontLibrary.arraySize = fonts.Count;
        fontSizeModifiers.arraySize = fonts.Count;

        for (int i = 0; i < fonts.Count; i++)
        {
            TMP_FontAsset font = fonts[i];
            fontLibrary.GetArrayElementAtIndex(i).objectReferenceValue = font;

            SerializedProperty modifierEntry = fontSizeModifiers.GetArrayElementAtIndex(i);
            modifierEntry.FindPropertyRelative("font").objectReferenceValue = font;
            modifierEntry.FindPropertyRelative("modifier").floatValue = existingModifiers.TryGetValue(font, out float modifier)
                ? modifier
                : 0f;
        }

        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private void DrawFontAssetDropdown(string label, SerializedProperty property)
    {
        TMP_FontAsset current = property.objectReferenceValue as TMP_FontAsset;
        List<TMP_FontAsset> choices = new List<TMP_FontAsset> { null };

        if (current != null && !fonts.Contains(current))
        {
            choices.Add(current);
        }

        choices.AddRange(fonts);

        string[] names = new string[choices.Count];
        for (int i = 0; i < choices.Count; i++)
        {
            names[i] = choices[i] == null ? "None" : CleanFontName(choices[i].name);
        }

        int currentIndex = choices.IndexOf(current);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        int newIndex = EditorGUILayout.Popup(label, currentIndex, names);
        if (newIndex != currentIndex)
        {
            property.objectReferenceValue = choices[newIndex];
        }
    }

    private string CleanFontName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unnamed Font";
        }

        string result = value.Trim();
        if (result.EndsWith(" SDF", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(0, result.Length - 4);
        }
        else if (result.EndsWith("_SDF", StringComparison.OrdinalIgnoreCase) ||
                 result.EndsWith("-SDF", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(0, result.Length - 4);
        }

        return result;
    }

    private string NormaliseFontName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string cleaned = CleanFontName(value);
        char[] buffer = new char[cleaned.Length];
        int count = 0;

        foreach (char character in cleaned)
        {
            if (char.IsWhiteSpace(character) || character == '_' || character == '-')
            {
                continue;
            }

            buffer[count++] = char.ToLowerInvariant(character);
        }

        return new string(buffer, 0, count);
    }
}
