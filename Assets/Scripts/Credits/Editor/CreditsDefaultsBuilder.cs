#if UNITY_EDITOR
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click setup for the credits:
///   Tools > Derelict Deliveries > Credits > 1. Fix Credits Image Imports
///   Tools > Derelict Deliveries > Credits > 2. Build Default Credits Assets
///
/// Step 2 creates the CreditsPerson assets and the CreditsRoll with the team
/// and asset credits filled in. It never overwrites an asset that already
/// exists, so once you have edited them, running it again is harmless (delete
/// an asset first if you want it regenerated).
///
///   Tools > Derelict Deliveries > Credits > 3. Relink Missing Credits Images
/// re-runs the import fix, then fills any EMPTY avatar / link icon / background
/// slot on the existing assets. Filled slots are never changed.
/// </summary>
public static class CreditsDefaultsBuilder
{
    private const string AvatarFolder = "Assets/Sprites/Credits/Avatars";
    private const string BackgroundFolder = "Assets/Sprites/Credits/Backgrounds";
    private const string DataFolder = "Assets/Data/Credits";
    private const string PeopleFolder = "Assets/Data/Credits/People";
    private const string FontFolder = "Assets/Fonts";
    private const string MenuRoot = "Tools/Derelict Deliveries/Credits/";


    // ------------------------------------------------------------------
    // 1. Import settings
    // ------------------------------------------------------------------

    [MenuItem(MenuRoot + "1. Fix Credits Image Imports")]
    public static void FixImports()
    {
        int changed = 0;
        changed += FixFolder(AvatarFolder, 512, false);
        changed += FixFolder(BackgroundFolder, 2048, true);
        AssetDatabase.Refresh();
        Debug.Log($"Credits: import settings checked, {changed} image(s) updated.");
    }


    private static int FixFolder(string folder, int maxSize, bool isBackground)
    {
        if (!AssetDatabase.IsValidFolder(folder))
        {
            Debug.LogWarning("Credits: folder not found: " + folder);
            return 0;
        }

        int changed = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            // Pixel art stays crisp; painted avatars and logos smooth.
            bool pixelArt = isBackground || Path.GetFileName(path).ToLowerInvariant().Contains("pixel");

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = pixelArt ? FilterMode.Point : FilterMode.Bilinear;
            importer.maxTextureSize = maxSize;
            importer.textureCompression = isBackground ? TextureImporterCompression.Uncompressed
                                                        : TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
            changed++;
        }
        return changed;
    }


    // ------------------------------------------------------------------
    // 2. Default assets
    // ------------------------------------------------------------------

    [MenuItem(MenuRoot + "2. Build Default Credits Assets")]
    public static void BuildDefaults()
    {
        EnsureFolder(DataFolder);
        EnsureFolder(PeopleFolder);

        // ---- Team 4 / Team Derelict (no avatars yet: initials show) ----
        CreditsPerson dean = Person("Team_DeanKennedy", p =>
        {
            p.displayName = "Dean Kennedy";
            p.idLine = "s224318581";
            p.contribution = "Pause menu prototype, the combat system and overall gameplay design. " +
                             "Also the one managing scope and keeping us on task.";
        });

        CreditsPerson matt = Person("Team_MattMurrell", p =>
        {
            p.displayName = "Matt Murrell";
            p.idLine = "mimurrell";
            p.contribution = "Design, and wrote most of the narrative and story.";
        });

        CreditsPerson jaali = Person("Team_JaaliBrennan", p =>
        {
            p.displayName = "Jaali Brennan";
            p.idLine = "s224475824";
            p.contribution = "Overall design and documentation.";
        });

        CreditsPerson oscar = Person("Team_OscarCarterSouth", p =>
        {
            p.displayName = "Oscar Carter-South";
            p.idLine = "s223125494";
            p.contribution = "Programming and meeting organisation.";
        });

        // ---- Art ----
        CreditsPerson rowany = Person("Art_RowanyMills", p =>
        {
            p.displayName = "Rowany Mills";
            p.idLine = "@vellichor42";
            p.contribution = "Character sprites, event banners and UI frames.";
            p.citation = "Mills, R. (2026). Derelict Deliveries character sprites, event banners and UI frames " +
                         "[Original artwork based on concepts and narrative by Team 4]. Instagram. " +
                         "https://www.instagram.com/vellichor42";
            p.avatar = Avatar("Avatar_Vellichor.jpg");
            p.links.Add(Link("instagram.com/vellichor42", "https://www.instagram.com/vellichor42"));
        });

        // ---- Music ----
        CreditsPerson robotmeadow = Person("Music_Robotmeadow", p =>
        {
            p.displayName = "William Javier";
            p.idLine = "@robotmeadow";
            p.contribution = "Background music: Ambient Themes.";
            p.note = "Free to use with no credit required - credited anyway, because it's great.";
            p.avatar = Avatar("Avatar_Robotmeadow.jpg");
            p.links.Add(Link("Ambient Themes on itch.io", "https://robotmeadows.itch.io/ambient-themes"));
            p.links.Add(Link("robotmeadows.itch.io", "https://robotmeadows.itch.io/"));
            p.links.Add(Link("x.com/robotmeadow", "https://x.com/robotmeadow"));
        });

        // ---- Assets ----
        CreditsPerson kenney = Person("Asset_Kenney", p =>
        {
            p.displayName = "Kenney";
            p.idLine = "kenney.nl";
            p.contribution = "Kenney Fonts - every font in the game.";
            p.citation = "Kenney. (2014). Kenney fonts [Font pack]. Kenney. https://kenney.nl/assets/kenney-fonts";
            p.avatar = Avatar("Avatar_Kenney.png");
            p.links.Add(Link("kenney.nl/assets/kenney-fonts", "https://kenney.nl/assets/kenney-fonts"));
        });

        Sprite poppantsItch = Avatar("Avatar_Poppants_Itch_pixel.png");
        Sprite poppantsConstruct = Avatar("Avatar_Poppants_Construct_pixel.jpg");
        CreditsPerson poppants = Person("Asset_Poppants", p =>
        {
            p.displayName = "Poppants";
            p.idLine = "itch.io";
            p.contribution = "8x8 Space Tilemap - the space tiles and sprites.";
            p.citation = "Poppants. (2021). 8x8 space tilemap [Sprite and tile set]. itch.io. " +
                         "https://poppants.itch.io/8x8-space-tilemap";
            p.avatar = poppantsItch;
            p.links.Add(Link("8x8 Space Tilemap", "https://poppants.itch.io/8x8-space-tilemap"));
            p.links.Add(Link("itch.io/profile/poppants", "https://itch.io/profile/poppants", poppantsItch));
            p.links.Add(Link("Poppants on Construct", "https://www.construct.net/en/users/232813/poppants/games", poppantsConstruct));
        });

        CreditsPerson wenrexa = Person("Asset_Wenrexa", p =>
        {
            p.displayName = "Wenrexa";
            p.idLine = "wenrexa.com";
            p.contribution = "UI Different 03 - interface asset pack.";
            p.citation = "Wenrexa. (2020). UI different 03 [User interface asset pack]. itch.io. " +
                         "https://wenrexa.itch.io/ui-different03";
            p.avatar = Avatar("Avatar_Wenrexa_Logo.png");   // white logo on transparent
            p.avatarFit = AvatarFit.Contain;
            p.avatarZoom = 0.8f;
            p.links.Add(Link("UI Different 03", "https://wenrexa.itch.io/ui-different03",
                             Avatar("Avatar_Wenrexa_Itch.png"), Color.white));   // black logo: needs white behind it
            p.links.Add(Link("wenrexa.com", "https://wenrexa.com/"));
        });

        // ---- Roll ----
        string rollPath = DataFolder + "/CreditsRoll.asset";
        if (AssetDatabase.LoadAssetAtPath<CreditsRoll>(rollPath) == null)
        {
            CreditsRoll roll = ScriptableObject.CreateInstance<CreditsRoll>();
            roll.title = "DERELICT DELIVERIES";
            roll.tagline = "SIT254 - Team 4 / Team Derelict";

            roll.sections.Add(Section("// TEAM DERELICT",
                "We all worked on the design - these are the bits each of us owned.",
                dean, matt, jaali, oscar));
            roll.sections.Add(Section("// ART", "", rowany));
            roll.sections.Add(Section("// MUSIC", "", robotmeadow));
            roll.sections.Add(Section("// ASSETS", "", kenney, poppants, wenrexa));

            roll.closingLine = "Thanks for playing.";

            roll.titleFont = Font("Kenney Future SDF");
            roll.headingFont = Font("Kenney Future SDF");
            roll.nameFont = Font("Kenney Future Narrow SDF");
            roll.numberFont = Font("Kenney Mini Square Mono SDF");
            roll.bodyFont = Font("Kenney Mini Square SDF");
            roll.linkFont = Font("Kenney Mini Square SDF");

            roll.background = LoadSprite(BackgroundFolder + "/Credits_Starfield_1080p.png");
            roll.terminalStyle = FindTerminalStyle();

            AssetDatabase.CreateAsset(roll, rollPath);
            Debug.Log("Credits: created " + rollPath, roll);
        }
        else
        {
            Debug.Log("Credits: " + rollPath + " already exists - left alone.");
        }

        AssetDatabase.SaveAssets();
    }


    // ------------------------------------------------------------------
    // 3. Relink
    // ------------------------------------------------------------------

    [MenuItem(MenuRoot + "3. Relink Missing Credits Images")]
    public static void RelinkImages()
    {
        FixImports();

        int fixedCount = 0;
        fixedCount += SetAvatar("Art_RowanyMills", "Avatar_Vellichor.jpg");
        fixedCount += SetAvatar("Music_Robotmeadow", "Avatar_Robotmeadow.jpg");
        fixedCount += SetAvatar("Asset_Kenney", "Avatar_Kenney.png");
        fixedCount += SetAvatar("Asset_Poppants", "Avatar_Poppants_Itch_pixel.png");
        fixedCount += SetAvatar("Asset_Wenrexa", "Avatar_Wenrexa_Logo.png");
        fixedCount += SetLinkIcon("Asset_Poppants", "itch.io/profile", "Avatar_Poppants_Itch_pixel.png", Color.clear);
        fixedCount += SetLinkIcon("Asset_Poppants", "construct.net", "Avatar_Poppants_Construct_pixel.jpg", Color.clear);
        fixedCount += SetLinkIcon("Asset_Wenrexa", "ui-different03", "Avatar_Wenrexa_Itch.png", Color.white);

        CreditsRoll roll = AssetDatabase.LoadAssetAtPath<CreditsRoll>(DataFolder + "/CreditsRoll.asset");
        if (roll != null && roll.background == null)
        {
            roll.background = LoadSprite(BackgroundFolder + "/Credits_Starfield_1080p.png");
            if (roll.background != null)
            {
                EditorUtility.SetDirty(roll);
                fixedCount++;
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"Credits: relink done, {fixedCount} empty slot(s) filled. Any image still missing is listed above.");
    }


    private static int SetAvatar(string personFile, string imageFile)
    {
        CreditsPerson person = AssetDatabase.LoadAssetAtPath<CreditsPerson>(PeopleFolder + "/" + personFile + ".asset");
        if (person == null || person.avatar != null) return 0;

        person.avatar = Avatar(imageFile);
        if (person.avatar == null) return 0;
        EditorUtility.SetDirty(person);
        return 1;
    }


    private static int SetLinkIcon(string personFile, string urlContains, string imageFile, Color background)
    {
        CreditsPerson person = AssetDatabase.LoadAssetAtPath<CreditsPerson>(PeopleFolder + "/" + personFile + ".asset");
        if (person == null) return 0;

        CreditLink link = person.links.FirstOrDefault(l => l != null && l.icon == null &&
                                                           l.url != null && l.url.Contains(urlContains));
        if (link == null) return 0;

        link.icon = Avatar(imageFile);
        if (link.icon == null) return 0;
        link.iconBackground = background;
        EditorUtility.SetDirty(person);
        return 1;
    }


    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CreditsPerson Person(string fileName, System.Action<CreditsPerson> fill)
    {
        string path = PeopleFolder + "/" + fileName + ".asset";
        CreditsPerson existing = AssetDatabase.LoadAssetAtPath<CreditsPerson>(path);
        if (existing != null) return existing;

        CreditsPerson person = ScriptableObject.CreateInstance<CreditsPerson>();
        fill(person);
        AssetDatabase.CreateAsset(person, path);
        return person;
    }


    private static CreditsSection Section(string heading, string intro, params CreditsPerson[] people)
    {
        CreditsSection section = new CreditsSection { heading = heading, intro = intro };
        section.people.AddRange(people);
        return section;
    }


    private static CreditLink Link(string label, string url, Sprite icon = null, Color? background = null)
    {
        return new CreditLink
        {
            label = label,
            url = url,
            icon = icon,
            iconBackground = background ?? Color.clear
        };
    }


    private static Sprite Avatar(string fileName) => LoadSprite(AvatarFolder + "/" + fileName);


    /// <summary>Loads the sprite in an image file, and explains why if there isn't one.</summary>
    private static Sprite LoadSprite(string path)
    {
        // LoadAll also finds the sprite when a texture was imported as Sprite Mode = Multiple.
        Sprite sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
        if (sprite != null) return sprite;

        if (!File.Exists(path))
        {
            Debug.LogWarning($"Credits: file not found: {path} (check the name and folder).");
        }
        else
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            string type = importer != null ? importer.textureType + " / " + importer.spriteImportMode : "not imported as a texture";
            Debug.LogWarning($"Credits: {path} has no sprite (import: {type}). Set Texture Type = Sprite (2D and UI), Apply, then run step 3.");
        }
        return null;
    }


    private static TMP_FontAsset Font(string assetName)
    {
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontFolder + "/" + assetName + ".asset");
        if (font == null) Debug.LogWarning("Credits: font not found: " + assetName);
        return font;
    }


    private static TerminalStyle FindTerminalStyle()
    {
        string[] guids = AssetDatabase.FindAssets("t:TerminalStyle");
        return guids.Length > 0 ? AssetDatabase.LoadAssetAtPath<TerminalStyle>(AssetDatabase.GUIDToAssetPath(guids[0])) : null;
    }


    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
#endif
