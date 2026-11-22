# BtSwitch

**Bascule le son de tes écouteurs Bluetooth entre ton PC Windows et ton téléphone, en un clic, sans les désappairer.**

*Switch your Bluetooth headphones between your Windows PC and your phone in one click, without unpairing them.*

D'habitude, pour passer tes écouteurs du PC au téléphone, il faut aller dans les paramètres Bluetooth, déconnecter, puis reconnecter dans l'autre sens. BtSwitch fait ça en un clic.

- Liste les appareils audio Bluetooth appairés et indique où va le son.
- **Vers le tél.** : le PC lâche les écouteurs, le téléphone peut les reprendre.
- **Vers le PC** : le PC les reconnecte.
- Icône dans la zone de notification : bleue quand le son est sur le PC, verte quand il est libre.
- Raccourci global **Ctrl+Alt+B** pour basculer le dernier appareil utilisé.
- Option « Lancer au démarrage de Windows ».

## Installation

Télécharge `BtSwitch.exe` depuis la page [Releases](https://github.com/Gustor1/btswitch/releases) et lance-le. Pas d'installation, un seul fichier.

> Windows SmartScreen peut avertir, car l'exécutable n'est pas signé. Tu peux aussi le compiler toi-même (ci-dessous).

## Compiler soi-même

Aucun SDK à installer : le compilateur C# fourni avec Windows (.NET Framework 4) suffit.

```bat
build.bat
```

ou directement :

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:btswitch.ico /out:BtSwitch.exe BtSwitch.cs
```

## Comment ça marche

BtSwitch utilise la propriété Kernel Streaming `KSPROPSETID_BtAudio` (`ONESHOT_DISCONNECT` / `ONESHOT_RECONNECT`) exposée par la pile audio Bluetooth de Windows, la même que Windows utilise en interne pour les boutons « Connecter / Déconnecter » des appareils audio. L'appairage n'est jamais modifié.

Les réglages (dernier appareil utilisé) sont stockés dans `HKCU\Software\BtSwitch`, et le lancement au démarrage dans `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Limites

- Windows 10 / 11 uniquement.
- Le téléphone ne récupère pas toujours les écouteurs automatiquement : selon le modèle, il peut falloir les sélectionner dans le menu Bluetooth du téléphone.

## Licence

[MIT](LICENSE)
