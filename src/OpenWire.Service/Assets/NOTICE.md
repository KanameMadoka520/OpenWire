# GeoIP data

OpenWire bundles the **DB-IP IP-to-Country Lite** database (`dbip-country-lite.mmdb`)
for offline country attribution.

<https://db-ip.com/db/lite.php> — © DB-IP, licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/).

To use a different / newer database, drop a MaxMind GeoLite2-Country `.mmdb` at
`%ProgramData%\OpenWire\GeoLite2-Country.mmdb`; it takes precedence over the bundled one.
