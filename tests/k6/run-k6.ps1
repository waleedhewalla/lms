$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-Content "$ScriptPath\load.js" | docker run --rm -i --add-host=host.docker.internal:host-gateway grafana/k6 run -
