using System.Globalization;

namespace AIOrchestrator.API;

/// <summary>
/// Localized user-facing messages for FreeCADTool's automatic setup (starting
/// FreeCAD, installing FCGear). The language is the OS UI language
/// (<see cref="CultureInfo.CurrentUICulture"/>), falling back to English for any
/// unsupported language — the same rule AgentBridge uses. Keys cover the cases a
/// non-technical user needs to understand when the tool cannot proceed silently.
/// </summary>
internal static class FreeCADStrings
{
    /// <summary>Two-letter OS UI language, "en" when invariant/unsupported.</summary>
    private static string Lang
    {
        get
        {
            try
            {
                var l = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                if (!string.IsNullOrWhiteSpace(l) && !l.Equals("iv", StringComparison.OrdinalIgnoreCase))
                    return l;
            }
            catch { }
            return "en";
        }
    }

    /// <summary>Localized body for a message key (English when the key or language is unknown).</summary>
    public static string Body(string key)
    {
        var lang = Lang;
        if (Table.TryGetValue(key, out var byLang))
        {
            if (byLang.TryGetValue(lang, out var s)) return s;
            if (byLang.TryGetValue("en", out var en)) return en;
        }
        return key;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        ["FreecadNotFound"] = new()
        {
            ["en"] = "FreeCAD is required for CAD work but was not found on this computer. Please install FreeCAD 1.x from freecad.org, then try again.",
            ["it"] = "FreeCAD è necessario per il CAD ma non è stato trovato su questo computer. Installa FreeCAD 1.x da freecad.org e riprova.",
            ["fr"] = "FreeCAD est nécessaire pour la CAO mais est introuvable sur cet ordinateur. Veuillez installer FreeCAD 1.x depuis freecad.org, puis réessayer.",
            ["es"] = "Se necesita FreeCAD para trabajar con CAD, pero no se encontró en este equipo. Instala FreeCAD 1.x desde freecad.org y vuelve a intentarlo.",
            ["de"] = "FreeCAD ist für CAD-Arbeiten erforderlich, wurde auf diesem Computer jedoch nicht gefunden. Bitte installieren Sie FreeCAD 1.x von freecad.org und versuchen Sie es erneut.",
            ["ru"] = "Для работы с CAD требуется FreeCAD, но он не найден на этом компьютере. Установите FreeCAD 1.x с freecad.org и повторите попытку.",
        },
        ["BridgeStarting"] = new()
        {
            ["en"] = "Starting FreeCAD in the background for CAD operations. This may take a few seconds…",
            ["it"] = "Avvio di FreeCAD in background per le operazioni CAD. Può richiedere alcuni secondi…",
            ["fr"] = "Démarrage de FreeCAD en arrière-plan pour les opérations de CAO. Cela peut prendre quelques secondes…",
            ["es"] = "Iniciando FreeCAD en segundo plano para las operaciones de CAD. Puede tardar unos segundos…",
            ["de"] = "FreeCAD wird im Hintergrund für CAD-Operationen gestartet. Das kann ein paar Sekunden dauern…",
            ["ru"] = "Запуск FreeCAD в фоновом режиме для операций CAD. Это может занять несколько секунд…",
        },
        ["BridgeFailed"] = new()
        {
            ["en"] = "Could not start FreeCAD automatically. Please open FreeCAD and try again.",
            ["it"] = "Impossibile avviare FreeCAD automaticamente. Apri FreeCAD e riprova.",
            ["fr"] = "Impossible de démarrer FreeCAD automatiquement. Veuillez ouvrir FreeCAD et réessayer.",
            ["es"] = "No se pudo iniciar FreeCAD automáticamente. Abre FreeCAD e inténtalo de nuevo.",
            ["de"] = "FreeCAD konnte nicht automatisch gestartet werden. Bitte öffnen Sie FreeCAD und versuchen Sie es erneut.",
            ["ru"] = "Не удалось автоматически запустить FreeCAD. Откройте FreeCAD и повторите попытку.",
        },
        ["GearInstalling"] = new()
        {
            ["en"] = "Installing the FCGear add-on so the agent can create gears. This runs in the background…",
            ["it"] = "Installazione del componente aggiuntivo FCGear per consentire all'agente di creare ingranaggi. Avviata in background…",
            ["fr"] = "Installation de l'extension FCGear pour que l'agent puisse créer des engrenages. En cours en arrière-plan…",
            ["es"] = "Instalando el complemento FCGear para que el agente pueda crear engranajes. Se ejecuta en segundo plano…",
            ["de"] = "Das Add-on FCGear wird installiert, damit der Agent Zahnräder erstellen kann. Läuft im Hintergrund…",
            ["ru"] = "Установка надстройки FCGear, чтобы агент мог создавать шестерни. Выполняется в фоновом режиме…",
        },
        ["GearInstalled"] = new()
        {
            ["en"] = "FCGear installed — gear creation is now available.",
            ["it"] = "FCGear installato — ora è possibile creare ingranaggi.",
            ["fr"] = "FCGear installé — la création d'engrenages est désormais disponible.",
            ["es"] = "FCGear instalado: ahora se pueden crear engranajes.",
            ["de"] = "FCGear installiert — die Erstellung von Zahnrädern ist jetzt verfügbar.",
            ["ru"] = "FCGear установлен — создание шестерён теперь доступно.",
        },
        ["GearFailed"] = new()
        {
            ["en"] = "Could not install FCGear automatically. To create gears, install FCGear from the FreeCAD Addon Manager.",
            ["it"] = "Impossibile installare FCGear automaticamente. Per creare ingranaggi, installa FCGear dal FreeCAD Addon Manager.",
            ["fr"] = "Impossible d'installer FCGear automatiquement. Pour créer des engrenages, installez FCGear depuis le FreeCAD Addon Manager.",
            ["es"] = "No se pudo instalar FCGear automáticamente. Para crear engranajes, instala FCGear desde el FreeCAD Addon Manager.",
            ["de"] = "FCGear konnte nicht automatisch installiert werden. Um Zahnräder zu erstellen, installieren Sie FCGear über den FreeCAD Addon Manager.",
            ["ru"] = "Не удалось автоматически установить FCGear. Чтобы создавать шестерни, установите FCGear через FreeCAD Addon Manager.",
        },
        ["ChatReady"] = new()
        {
            ["en"] = "AgentBridge chat installed in FreeCAD. Open (or restart) FreeCAD to chat with the assistant from inside it.",
            ["it"] = "Chat di AgentBridge installata in FreeCAD. Apri (o riavvia) FreeCAD per chattare con l'assistente da dentro.",
            ["fr"] = "Chat AgentBridge installé dans FreeCAD. Ouvrez (ou redémarrez) FreeCAD pour discuter avec l'assistant depuis l'intérieur.",
            ["es"] = "Chat de AgentBridge instalado en FreeCAD. Abre (o reinicia) FreeCAD para chatear con el asistente desde dentro.",
            ["de"] = "AgentBridge-Chat in FreeCAD installiert. Öffnen (oder neu starten) Sie FreeCAD, um darin mit dem Assistenten zu chatten.",
            ["ru"] = "Чат AgentBridge установлен в FreeCAD. Откройте (или перезапустите) FreeCAD, чтобы общаться с помощником прямо из него.",
        },
    };
}
