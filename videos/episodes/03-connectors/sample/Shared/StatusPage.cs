using Coffee;

/// <summary>
/// Status page designed for video, shared by the server and the client: an 800 by 720 column with large type
/// and the video palette, polling <c>/status</c> every 250 ms. Two pages fit side by side in one recording.
/// </summary>
public static class StatusPage
{
    /// <summary>The JSON the page polls; <paramref name="connection"/> is the label of the role pill.</summary>
    public static object Status(CoffeeMachine machine, string connection) => new
    {
        machine.Status,
        machine.IsReady,
        machine.Boiler.Temperature,
        machine.Boiler.TargetTemperature,
        machine.Pump.Pressure,
        machine.CupsBrewed,
        WaterLevel = machine.WaterTank.Level,
        Connection = connection
    };

    /// <summary>Renders the page for one process; <paramref name="accent"/> colors the role dot.</summary>
    public static string Render(string role, string accent, bool canBrew) => Html
        .Replace("{{role}}", role)
        .Replace("{{accent}}", accent)
        .Replace("{{brew}}", canBrew
            ? """<button id="brew" onclick="brew()">Brew Espresso</button>"""
            : "");

    private const string Html = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Coffee Machine</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body {
              margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center; overflow: hidden;
              background: #1c1c1e; color: #f5f5f7; font-family: Inter, system-ui, sans-serif;
            }
            main { width: 704px; }
            .top { display: flex; justify-content: space-between; align-items: center; }
            .kicker { display: flex; align-items: center; gap: 12px; font-size: 22px; font-weight: 600; letter-spacing: 0.2em; color: #a1a1a6; text-transform: uppercase; }
            .role-dot { width: 14px; height: 14px; border-radius: 50%; background: {{accent}}; }
            .connection { font-size: 22px; font-weight: 600; color: #a1a1a6; display: flex; align-items: center; gap: 10px; }
            .connection .dot { width: 12px; height: 12px; border-radius: 50%; background: #30d158; }
            h1 { margin: 14px 0 26px; font-size: 72px; font-weight: 700; letter-spacing: -0.03em; line-height: 1.05; white-space: nowrap; }
            .card {
              background: #2c2c2e; border-radius: 28px; padding: 24px 36px 30px; margin-bottom: 18px;
              box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
            }
            .row { display: flex; justify-content: space-between; align-items: baseline; }
            .label { font-size: 28px; font-weight: 500; color: #a1a1a6; }
            .value { font-size: 56px; font-weight: 600; font-variant-numeric: tabular-nums; }
            .bar { margin-top: 16px; height: 16px; border-radius: 8px; background: #3a3a3c; overflow: hidden; }
            .fill { height: 100%; width: 0; border-radius: 8px; transition: width 0.45s ease, background 0.6s ease; }
            #temperature-fill { background: #ff9f0a; }
            #pressure-fill { background: #64d2ff; }
            .ready #temperature-fill { background: #30d158; }
            .bottom { display: flex; justify-content: space-between; align-items: center; margin-top: 26px; }
            .pills { display: flex; gap: 14px; }
            .pill {
              display: inline-flex; align-items: baseline; gap: 10px; padding: 14px 24px; border-radius: 999px;
              background: #2c2c2e; font-size: 26px; font-weight: 600; font-variant-numeric: tabular-nums;
              box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
            }
            .pill span { color: #a1a1a6; font-weight: 500; }
            button {
              font: inherit; font-size: 26px; font-weight: 600; color: #ffffff; background: #0a84ff; border: 0;
              border-radius: 999px; padding: 16px 34px; box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
              transition: background 0.3s ease, transform 0.15s ease;
            }
            button:active { transform: scale(0.96); }
            button:disabled { background: #3a3a3c; color: #a1a1a6; }
          </style>
        </head>
        <body>
          <main id="page">
            <div class="top">
              <div class="kicker"><span class="role-dot"></span>{{role}}</div>
              <div class="connection"><span class="dot"></span><span id="connection">Connecting</span></div>
            </div>
            <h1 id="status">Connecting</h1>
            <div class="card">
              <div class="row"><span class="label">Boiler</span><span class="value" id="temperature">-- °C</span></div>
              <div class="bar"><div class="fill" id="temperature-fill"></div></div>
            </div>
            <div class="card">
              <div class="row"><span class="label">Pump</span><span class="value" id="pressure">-- bar</span></div>
              <div class="bar"><div class="fill" id="pressure-fill"></div></div>
            </div>
            <div class="bottom">
              <div class="pills">
                <div class="pill"><span>Cups</span><b id="cups">0</b></div>
                <div class="pill"><span>Water</span><b id="water">--</b></div>
              </div>
              {{brew}}
            </div>
          </main>
          <script>
            let ready = false;
            async function update() {
              try {
                const response = await fetch('/status');
                const machine = await response.json();
                ready = machine.isReady;
                document.getElementById('status').textContent = machine.status;
                document.getElementById('connection').textContent = machine.connection;
                document.getElementById('temperature').textContent = machine.temperature.toFixed(1) + ' °C';
                const heat = Math.min(Math.max((machine.temperature - 20) / (machine.targetTemperature - 20), 0), 1);
                document.getElementById('temperature-fill').style.width = (heat * 100).toFixed(1) + '%';
                document.getElementById('pressure').textContent = machine.pressure.toFixed(1) + ' bar';
                document.getElementById('pressure-fill').style.width = (Math.min(machine.pressure / 9, 1) * 100).toFixed(1) + '%';
                document.getElementById('cups').textContent = machine.cupsBrewed;
                document.getElementById('water').textContent = machine.waterLevel.toFixed(0) + ' %';
                document.getElementById('page').classList.toggle('ready', machine.isReady);
                const button = document.getElementById('brew');
                if (button) button.disabled = !machine.isReady;
              } catch {
                // The next poll retries.
              }
            }
            async function brew() {
              await fetch('/brew/Espresso', { method: 'POST' });
              update();
            }
            update();
            setInterval(update, 250);
          </script>
        </body>
        </html>
        """;
}
