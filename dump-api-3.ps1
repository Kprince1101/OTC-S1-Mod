$prof = "$env:APPDATA\r2modmanPlus-local\ScheduleI\profiles\Legion Schedule 1"
Add-Type -Path "$prof\MelonLoader\net6\Mono.Cecil.dll"
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly("$prof\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll")
$all = $asm.MainModule.GetTypes()

function Dump-Type($t, $sb) {
  [void]$sb.AppendLine("===== " + $t.FullName + " =====")
  foreach ($p in $t.Properties) {
    $getVis = "-"
    if ($p.GetMethod) { if ($p.GetMethod.IsPublic) { $getVis = "pub" } else { $getVis = "priv" } }
    $setVis = "-"
    if ($p.SetMethod) { if ($p.SetMethod.IsPublic) { $setVis = "pub" } else { $setVis = "priv" } }
    [void]$sb.AppendLine("  PROP  " + $p.PropertyType.Name + " " + $p.Name + "  get=" + $getVis + " set=" + $setVis)
  }
  foreach ($f in $t.Fields) {
    if ($f.Name.StartsWith('NativeField') -or $f.Name.StartsWith('NativeMethod')) { continue }
    $vis = "other"
    if ($f.IsPublic) { $vis = "pub" } elseif ($f.IsPrivate) { $vis = "priv" }
    [void]$sb.AppendLine("  FIELD " + $f.FieldType.Name + " " + $f.Name + "  " + $vis + " static=" + $f.IsStatic)
  }
  foreach ($m in $t.Methods) {
    if ($m.IsGetter -or $m.IsSetter -or $m.Name.StartsWith('Rpc')) { continue }
    $vis = "pub"; if (-not $m.IsPublic) { $vis = "priv" }
    $ps = ($m.Parameters | ForEach-Object { $_.ParameterType.Name + " " + $_.Name }) -join ', '
    [void]$sb.AppendLine("  METH  " + $vis + " " + $m.ReturnType.Name + " " + $m.Name + "(" + $ps + ")")
  }
  foreach ($n in $t.NestedTypes) {
    if ($n.Name -match '^__c|^_.*_d__|DisplayClass|Compiler') { continue }
    $vals = ($n.Fields | Where-Object { $_.IsStatic -and -not $_.Name.StartsWith('NativeField') } | ForEach-Object { $_.Name }) -join ', '
    [void]$sb.AppendLine("  NESTED " + $n.Name + ": " + $vals)
  }
  [void]$sb.AppendLine()
}

$sb = New-Object System.Text.StringBuilder

[void]$sb.AppendLine("===== SEARCH: types named Interaction, Inventory, StorageEntity (all namespaces) =====")
$all | Where-Object { $_.Name -eq 'Interaction' -or $_.Name -eq 'Inventory' -or $_.Name -eq 'StorageEntity' } | ForEach-Object { [void]$sb.AppendLine("  TYPE " + $_.FullName) }
[void]$sb.AppendLine("")

[void]$sb.AppendLine("===== SEARCH: types with StorageEntity in the name =====")
$all | Where-Object { $_.Name -match 'StorageEntity' } | ForEach-Object { [void]$sb.AppendLine("  TYPE " + $_.FullName) }
[void]$sb.AppendLine("")

$interaction = $all | Where-Object { $_.FullName -eq 'Il2CppScheduleOne.NPCs.Framework.Interaction' } | Select-Object -First 1
if ($interaction) { Dump-Type $interaction $sb } else { [void]$sb.AppendLine("===== Framework.Interaction -- EXACT NAME NOT FOUND, see SEARCH above =====`n") }

$inventory = $all | Where-Object { $_.FullName -eq 'Il2CppScheduleOne.NPCs.Framework.Inventory' } | Select-Object -First 1
if ($inventory) { Dump-Type $inventory $sb } else { [void]$sb.AppendLine("===== Framework.Inventory -- EXACT NAME NOT FOUND, see SEARCH above =====`n") }

# StorageEntity namespace is unconfirmed -- try a few likely candidates, dump whichever exist
$storageCandidates = @(
  'Il2CppScheduleOne.Storage.StorageEntity',
  'Il2CppScheduleOne.Property.StorageEntity',
  'Il2CppScheduleOne.Employees.StorageEntity',
  'Il2CppScheduleOne.ItemFramework.StorageEntity'
)
foreach ($cand in $storageCandidates) {
  $t = $all | Where-Object { $_.FullName -eq $cand } | Select-Object -First 1
  if ($t) { Dump-Type $t $sb }
}
# fallback: dump every type whose short name is exactly StorageEntity, in case none of the above matched
$all | Where-Object { $_.Name -eq 'StorageEntity' } | ForEach-Object { Dump-Type $_ $sb }

[void]$sb.AppendLine("===== EmployeeHome full dump (re-check Storage property type) =====")
$eh = $all | Where-Object { $_.Name -eq 'EmployeeHome' } | Select-Object -First 1
if ($eh) { Dump-Type $eh $sb }

[void]$sb.AppendLine("===== SEARCH: Deposit or AddCash or AddItem across ALL types =====")
foreach ($t in $all) {
  foreach ($m in $t.Methods) { if ($m.Name -match 'Deposit|AddCash|AddItem|InsertItem|StoreItem' -and -not $m.IsGetter -and -not $m.IsSetter) {
    [void]$sb.AppendLine("  " + $t.FullName + "." + $m.Name + "()") } }
}
[void]$sb.AppendLine("")

[System.IO.File]::WriteAllText("C:\Users\krist\Developer\api-dump-3.txt", $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Host ("wrote api-dump-3.txt (" + $sb.Length + " chars)")
