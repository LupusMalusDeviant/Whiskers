# SSH-Härtung der Flotte

Ablauf, wie er am 06.10.2026 auf **BurgCloud** gefahren wurde (BurgCloudMVC-Bean `ndrt`), als Vorlage für die
übrigen Server. Ziel: Anmeldung nur per Schlüssel, fail2ban wirksam, Port 22 nur über das Tailnet.

## Ausgangslage (Nightrun 05.10.2026, nur lesend geprüft)

| Server | Passwort-Anmeldung | fail2ban | Fehlversuche | Stand |
|---|---|---|---|---|
| BurgCloud (Debian 12) | war an | lief, sah aber nichts (las leere `auth.log`) | 87.921 seit 28.09., 379 in einer Stunde | **Schritt 1–2 erledigt 06.10.2026**, Schritt 3 wartet auf das Tailnet des Arbeitsrechners |
| Rabenhof (Ubuntu 26.04) | an | **inaktiv** | 416.009 seit 06.08. | offen |
| Zirkuswagen (Ubuntu 24.04) | an | **inaktiv** | Zählung lief in Timeout | offen |
| AppServer (Ubuntu 26.04) | an | **inaktiv** | 0 | offen |
| LupusMalus (Infomaniak) | nicht geprüft | nicht geprüft | – | prüfen |
| BurgCloud-Office | aus | – | – | schon gehärtet (nur Schlüssel, 22 nur Tailnet) |

Ein erratenes Passwort heißt dort root: Die Dienstkonten haben `sudo NOPASSWD: ALL` und sind in der Gruppe
`docker` (gleichbedeutend mit root). Wirksam sind deshalb nur Schlüssel-Anmeldung und kein öffentlicher Port 22;
sudo mit Passwort allein brächte wenig.

## 0. Bestandsaufnahme (nur lesend)

```bash
sudo sshd -T | grep -E '^(passwordauthentication|kbdinteractiveauthentication|permitrootlogin|maxauthtries) '
ls /etc/ssh/sshd_config.d/; grep -n '^Include' /etc/ssh/sshd_config
sudo journalctl -u ssh --since 2026-09-01 | grep -o 'Accepted [a-z-]* for [a-z0-9_-]* from [0-9a-f.:]*' | sort | uniq -c
systemctl is-active fail2ban; sudo fail2ban-client status sshd
sudo ss -tlnp            # wer lauscht öffentlich?
tailscale status         # ist der Server im Tailnet, ist Tailscale-SSH an (tailscale debug prefs | grep RunSSH)?
```

Vor jeder Änderung klären, **wer sich per SSH anmeldet** (Liste der „Accepted“-Zeilen): Arbeitsrechner, CI,
Whiskers. Whiskers verbindet sich mit Servern vom Typ `TCP` über die Docker-Schnittstelle auf der Tailnet-Adresse,
nicht über SSH; Server vom Typ `SSH` über das Tailnet.

## 1. Nur noch Schlüssel – mit automatischer Rücknahme

Nur weitermachen, wenn die Bestandsaufnahme **keine** Passwort-Anmeldungen zeigt, die noch gebraucht werden.

```bash
F=/etc/ssh/sshd_config.d/10-nur-schluessel.conf
sudo tee "$F" > /dev/null <<'KONF'
# Nur Anmeldung per Schluessel. Notzugang: Hetzner-Konsole, root-Passwort dort zuruecksetzen.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
MaxAuthTries 3
KONF
sudo sshd -t
# Rücknahme nach 5 Minuten, falls die neue Anmeldung scheitert:
sudo systemd-run --on-active=300 --unit=ssh-rueckfall /bin/sh -c "rm -f $F && systemctl reload ssh"
sudo systemctl reload ssh
```

- **Dateiname mit `10-`:** sshd nimmt den *ersten* gefundenen Wert. Ubuntu-Cloud-Images legen
  `50-cloud-init.conf` mit `PasswordAuthentication yes` an – eine Datei, die danach sortiert, wirkt nicht.
  Nachweis immer mit `sshd -T`, nicht mit dem Dateiinhalt.
- **Ubuntu ab 24.04** startet sshd oft per `ssh.socket`: Dann gilt die Datei für jede neue Verbindung, `reload`
  kann fehlschlagen – vorher `systemctl is-active ssh ssh.socket` prüfen.

Nachweis von außen, **mit einer neuen Verbindung**, bevor die Rücknahme angehalten wird:

```bash
ssh -o BatchMode=yes -o PubkeyAuthentication=no -o PreferredAuthentications=password,keyboard-interactive <server> true
#   vorher: Permission denied (publickey,password).   nachher: Permission denied (publickey).
ssh -o BatchMode=yes <server> 'echo ok'           # Schlüssel geht
ssh <server> 'sudo systemctl stop ssh-rueckfall.timer'
```

Verbindungen, die vor dem `reload` schon offen waren, laufen mit der alten Einstellung zu Ende – ein, zwei
„Failed password“ in den Sekunden danach sind kein Fehlschlag (BurgCloud: 2 Stück bis 19:14:51, danach keine).

## 2. fail2ban, das wirklich mitliest

Auf Debian 12 und Ubuntu schreibt sshd nur ins Journal; `/var/log/auth.log` bleibt leer, und fail2ban zählt
stumm null. Debian/Ubuntu nennen den Dienst `ssh.service`, der Standardfilter sucht `sshd.service`.
**Ab OpenSSH 9.8 (Ubuntu 26.04)** protokolliert der Anmeldeprozess als `sshd-session`.

```bash
sudo apt-get install -y python3-systemd fail2ban
sudo tee /etc/fail2ban/jail.d/sshd-journal.local > /dev/null <<'KONF'
[sshd]
enabled = true
backend = systemd
logpath =
journalmatch = _COMM=sshd + _COMM=sshd-session
ignoreip = 127.0.0.1/8 ::1 100.64.0.0/10
KONF
sudo fail2ban-client -t && sudo systemctl enable --now fail2ban && sudo systemctl restart fail2ban
sudo fail2ban-client status sshd     # "Journal matches" gesetzt, "Total failed" steigt
```

`100.64.0.0/10` (Tailnet) steht in `ignoreip`, damit Whiskers nie gesperrt wird. Nachweis: Nach einer Minute
muss `Total failed` > 0 sein, solange Port 22 öffentlich ist; der eigene Anschluss darf nicht in
`Banned IP list` stehen.

## 3. Port 22 nur über das Tailnet

Reihenfolge, damit sich niemand aussperrt:

1. Jeder, der sich anmeldet, ist im Tailnet (Arbeitsrechner, Whiskers/Badwolf) und erreicht `<tailnet-ip>:22`.
2. `~/.ssh/config` und Git-Remotes auf die Tailnet-Adresse bzw. den MagicDNS-Namen umstellen, testen.
3. In der **Hetzner-Cloud-Firewall** die Regel für Port 22 entfernen (Whiskers kann nur UFW, nicht die
   Hetzner-Firewall). Docker-Ports umgehen UFW, deshalb die Hetzner-Firewall.
4. Von außen prüfen: `timeout 6 bash -c '</dev/tcp/<öffentliche-ip>/22'` muss scheitern, über das Tailnet klappt es.

CI-Deploys per SSH (GitHub Actions) brechen damit – bei BurgCloud gewollt: Deploy künftig über Whiskers, die
Workflows sind seit 06.10.2026 abgeschaltet.

## Notzugang

Wer kein Passwort kennt: Hetzner-Konsole → root-Passwort zurücksetzen → Anmeldung an der Web-Konsole. Die
Schlüssel-Einstellung betrifft nur SSH, nicht die Konsole.
