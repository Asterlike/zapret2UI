# Пересобирает search-index.js из содержимого страниц сайта.
# Запускать после любой правки текста, ОБА раза — у русской и английской версий
# свои индексы, иначе поиск на одном языке будет находить страницы другого:
#     powershell -ExecutionPolicy Bypass -File docs\build-search-index.ps1
#     powershell -ExecutionPolicy Bypass -File docs\build-search-index.ps1 -Source docs\en -Out docs\en\search-index.js
#
# Индекс лежит в репозитории готовым файлом, потому что GitHub Pages раздаёт
# статику как есть и собирать на сервере нечем. Скрипт нужен только автору.

param(
  # Папка со страницами. По умолчанию та, где лежит сам скрипт (русская версия).
  [string]$Source,
  # Куда писать индекс. По умолчанию assets\search-index.js рядом со страницами.
  [string]$Out
)

$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = if ($Source) { (Resolve-Path $Source).Path } else { $dir }
$outFile = if ($Out) { $Out } else { Join-Path $dir 'assets\search-index.js' }
$enc = New-Object System.Text.UTF8Encoding($false)

function Clean([string]$h) {
  $t = [regex]::Replace($h, '(?s)<(script|style)\b.*?</\1>', ' ')
  $t = [regex]::Replace($t, '<[^>]+>', ' ')
  $t = $t.Replace('&nbsp;', ' ').Replace('&amp;', '&').Replace('&lt;', '<').Replace('&gt;', '>').Replace('&quot;', '"')
  $t = [regex]::Replace($t, '\s+', ' ')
  return $t.Trim()
}

$entries = New-Object System.Collections.ArrayList
foreach ($f in (Get-ChildItem $src -Filter *.html | Sort-Object Name)) {
  $raw = [System.IO.File]::ReadAllText($f.FullName)
  $m = [regex]::Match($raw, '(?s)<main class="doc">(.*?)</main>')
  if (-not $m.Success) { continue }
  $body = $m.Groups[1].Value

  $h1 = [regex]::Match($body, '(?s)<h1[^>]*>(.*?)</h1>')
  $pageTitle = if ($h1.Success) { Clean $h1.Groups[1].Value } else { $f.BaseName }

  $heads = [regex]::Matches($body, '(?s)<h([23])\s+id="([^"]+)"[^>]*>(.*?)</h\1>')

  # запись на всю страницу: подзаголовок и вступление до первого раздела
  $intro = if ($heads.Count -gt 0) { $body.Substring(0, $heads[0].Index) } else { $body }
  [void]$entries.Add([pscustomobject]@{ p = $f.Name; pt = $pageTitle; id = ''; t = $pageTitle; s = (Clean $intro) })

  for ($i = 0; $i -lt $heads.Count; $i++) {
    $start = $heads[$i].Index + $heads[$i].Length
    $end = if ($i + 1 -lt $heads.Count) { $heads[$i + 1].Index } else { $body.Length }
    $txt = Clean $body.Substring($start, $end - $start)
    if ($txt.Length -gt 600) { $txt = $txt.Substring(0, 600) }
    $title = [regex]::Replace((Clean $heads[$i].Groups[3].Value), '\s*#$', '')
    [void]$entries.Add([pscustomobject]@{ p = $f.Name; pt = $pageTitle; id = $heads[$i].Groups[2].Value; t = $title; s = $txt })
  }
}

$json = $entries | ConvertTo-Json -Compress -Depth 3
$out = "/* Сгенерировано build-search-index.ps1 из страниц сайта. Не редактировать вручную. */`n" +
       "window.SEARCH_INDEX = $json;`n"
[System.IO.File]::WriteAllText($outFile, $out, $enc)

Write-Host "Записей: $($entries.Count)"
Write-Host "Файл: $outFile"
