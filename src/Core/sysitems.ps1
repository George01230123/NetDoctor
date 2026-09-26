param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('appx', 'tasks', 'hw')]
    [string]$Mode
)

$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Safe($scriptBlock) {
    try { return & $scriptBlock } catch { return $null }
}

if ($Mode -eq 'appx') {
    $list = @()
    $pkgs = Get-AppxPackage -ErrorAction SilentlyContinue
    foreach ($p in $pkgs) {
        if ($p.IsFramework) { continue }
        $size = 0
        try {
            if ($p.InstallLocation -and (Test-Path $p.InstallLocation)) {
                $size = (Get-ChildItem $p.InstallLocation -Recurse -File -Force |
                         Measure-Object -Property Length -Sum).Sum
            }
        } catch { }
        $list += [PSCustomObject]@{
            Name        = $p.Name
            FullName    = $p.PackageFullName
            Version     = $p.Version
            Publisher   = $p.Publisher
            SizeBytes   = [long]$size
            NonRemovable = [bool]$p.NonRemovable
        }
    }
    $list | ConvertTo-Json -Depth 3 -Compress
    exit 0
}

if ($Mode -eq 'tasks') {
    $list = @()
    foreach ($t in (Get-ScheduledTask -ErrorAction SilentlyContinue)) {
        $trig = ($t.Triggers | ForEach-Object { $_.CimClass.CimClassName }) -join ','
        $act  = ($t.Actions  | ForEach-Object { $_.Execute }) -join ' '
        if (-not $act) { $act = ($t.Actions | ForEach-Object { $_.CimClass.CimClassName }) -join ',' }
        $list += [PSCustomObject]@{
            TaskPath = $t.TaskPath
            TaskName = $t.TaskName
            State    = [string]$t.State
            Triggers = $trig
            Action   = $act
            Author   = $t.Author
        }
    }
    $list | ConvertTo-Json -Depth 3 -Compress
    exit 0
}

if ($Mode -eq 'hw') {
    $out = [ordered]@{}

    # ---------------- 系统 ----------------
    $os  = Get-CimInstance Win32_OperatingSystem
    $cs  = Get-CimInstance Win32_ComputerSystem
    $bios = Get-CimInstance Win32_BIOS
    $bb  = Get-CimInstance Win32_BaseBoard
    $enc = Get-CimInstance Win32_SystemEnclosure
    $out.system = [PSCustomObject]@{
        Caption      = $os.Caption
        Version      = $os.Version
        Build        = $os.BuildNumber
        Arch         = $os.OSArchitecture
        InstallDate  = "$($os.InstallDate)"
        LastBoot     = "$($os.LastBootUpTime)"
        ComputerName = $cs.Name
        UserName     = $cs.UserName
        Manufacturer = $cs.Manufacturer
        Model        = $cs.Model
        SystemType   = $cs.SystemType
        Chassis      = ($enc | Select-Object -First 1).ChassisTypes -join ','
        TotalRamBytes = [long]$cs.TotalPhysicalMemory
        BiosVendor   = $bios.Manufacturer
        BiosVersion  = $bios.SMBIOSBIOSVersion
        BiosDate     = $bios.ReleaseDate
        BoardVendor  = $bb.Manufacturer
        BoardProduct = $bb.Product
        BoardVersion = $bb.Version
        BoardSerial  = $bb.SerialNumber
        UUID         = $bios.SerialNumber
        SecureBoot   = (Safe { (Confirm-SecureBootUEFI) }) -as [string]
        Uptime       = "$((Get-Date) - $os.LastBootUpTime)"
    }

    # ---------------- CPU ----------------
    $cpus = @()
    foreach ($c in (Get-CimInstance Win32_Processor)) {
        $cpus += [PSCustomObject]@{
            Name          = $c.Name
            Manufacturer  = $c.Manufacturer
            Cores         = $c.NumberOfCores
            Threads       = $c.NumberOfLogicalProcessors
            MaxClockMHz   = $c.MaxClockSpeed
            CurrentMHz    = $c.CurrentClockSpeed
            ExtClockMHz   = $c.ExtClockSpeed
            L2KB          = $c.L2CacheSize
            L3KB          = $c.L3CacheSize
            Socket        = $c.SocketDesignation
            ProcessorId   = $c.ProcessorId
            Architecture  = $c.AddressWidth
            Virtualization= $c.VirtualizationFirmwareEnabled
            DataWidth     = $c.DataWidth
            LoadPercent   = $c.LoadPercentage
            Status        = $c.Status
        }
    }
    $out.cpu = $cpus

    # ---------------- 内存 ----------------
    $mods = @()
    foreach ($m in (Get-CimInstance Win32_PhysicalMemory)) {
        $typeName = switch ([int]$m.SMBIOSMemoryType) {
            20 { 'DDR' } 21 { 'DDR2' } 24 { 'DDR3' } 26 { 'DDR4' } 34 { 'DDR5' }
            0  { switch ([int]$m.MemoryType) { 20 {'DDR'} 21 {'DDR2'} 24 {'DDR3'} default { "未知($($m.MemoryType))" } } }
            default { "未知($($m.SMBIOSMemoryType))" }
        }
        $mods += [PSCustomObject]@{
            Bank         = $m.BankLabel
            DeviceLocator= $m.DeviceLocator
            CapacityBytes= [long]$m.Capacity
            SpeedMHz     = $m.Speed
            ConfiguredMHz= $m.ConfiguredClockSpeed
            Type         = $typeName
            FormFactor   = switch ([int]$m.FormFactor) { 8 {'DIMM'} 12 {'SODIMM'} 13 {'SRIMM'} default { "$($m.FormFactor)" } }
            Manufacturer = $m.Manufacturer
            PartNumber   = "$($m.PartNumber)".Trim()
            Serial       = $m.SerialNumber
            VoltageMV    = $m.ConfiguredVoltage
        }
    }
    $out.memory = $mods

    # ---------------- 显卡 ----------------
    $gpus = @()
    foreach ($g in (Get-CimInstance Win32_VideoController)) {
        $gpus += [PSCustomObject]@{
            Name          = $g.Name
            AdapterRam    = [long]$g.AdapterRAM
            DriverVersion = $g.DriverVersion
            DriverDate    = "$($g.DriverDate)"
            VideoProcessor= $g.VideoProcessor
            Resolution    = "$($g.CurrentHorizontalResolution)x$($g.CurrentVerticalResolution)"
            RefreshRate   = $g.CurrentRefreshRate
            BitsPerPixel  = $g.CurrentBitsPerPixel
            Status        = $g.Status
            PNPDeviceID   = $g.PNPDeviceID
        }
    }
    $out.gpu = $gpus

    # ---------------- 硬盘 ----------------
    $disks = @()
    foreach ($d in (Get-CimInstance Win32_DiskDrive)) {
        $rel = 0; $temp = $null; $hours = $null; $wear = $null; $powerOn = $null
        try {
            $p = Get-PhysicalDisk -DeviceNumber $d.Index -ErrorAction Stop
            $rel = $p.HealthStatus
            $rc = $p | Get-StorageReliabilityCounter -ErrorAction Stop
            $temp = $rc.Temperature; $hours = $rc.PowerOnHours; $wear = $rc.Wear
        } catch { }
        $disks += [PSCustomObject]@{
            Index        = $d.Index
            Model        = "$($d.Model)".Trim()
            Serial       = "$($d.SerialNumber)".Trim()
            Interface    = $d.InterfaceType
            MediaType    = $d.MediaType
            SizeBytes    = [long]$d.Size
            Partitions   = $d.Partitions
            Firmware     = $d.FirmwareRevision
            Health       = "$rel"
            Temperature  = $temp
            PowerOnHours = $hours
            Wear         = $wear
            Status       = $d.Status
        }
    }
    $out.disk = $disks

    # 分区/卷
    $vols = @()
    foreach ($v in (Get-CimInstance Win32_LogicalDisk)) {
        $vols += [PSCustomObject]@{
            Drive        = $v.DeviceID
            Label        = $v.VolumeName
            FileSystem   = $v.FileSystem
            SizeBytes    = [long]$v.Size
            FreeBytes    = [long]$v.FreeSpace
            DriveType    = switch ([int]$v.DriveType) { 2 {'可移动'} 3 {'本地磁盘'} 4 {'网络'} 5 {'光驱'} default {"$($v.DriveType)"} }
        }
    }
    $out.volume = $vols

    # ---------------- 显示器 ----------------
    $mons = @()
    foreach ($m in (Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorID)) {
        function Dec($arr) {
            if (-not $arr) { return '' }
            $s = ($arr | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) -join ''
            return $s.Trim()
        }
        $mons += [PSCustomObject]@{
            InstanceName = $m.InstanceName
            Manufacturer = (Dec $m.ManufacturerName)
            ProductCode  = (Dec $m.ProductCodeID)
            SerialNumber = (Dec $m.SerialNumberID)
            FriendlyName = (Dec $m.UserFriendlyName)
            YearOfMfg    = $m.YearOfManufacture
            WeekOfMfg    = $m.WeekOfManufacture
        }
    }
    $out.monitor = $mons

    # 显示器 EDID 物理尺寸
    $edids = @()
    foreach ($e in (Get-CimInstance -Namespace root\wmi -ClassName WmiMonitorBasicDisplayParams)) {
        $edids += [PSCustomObject]@{
            InstanceName = $e.InstanceName
            WidthCm      = [math]::Round([double]$e.MaxHorizontalImageSize, 1)
            HeightCm     = [math]::Round([double]$e.MaxVerticalImageSize, 1)
            DiagonalInch = [math]::Round([math]::Sqrt([math]::Pow([double]$e.MaxHorizontalImageSize,2) + [math]::Pow([double]$e.MaxVerticalImageSize,2)) / 2.54, 1)
        }
    }
    $out.monitorSize = $edids

    # ---------------- 网卡 ----------------
    $nets = @()
    foreach ($n in (Get-NetAdapter -ErrorAction SilentlyContinue)) {
        $nets += [PSCustomObject]@{
            Name      = $n.Name
            Desc      = $n.InterfaceDescription
            Status    = "$($n.Status)"
            LinkSpeed = $n.LinkSpeed
            Mac       = $n.MacAddress
        }
    }
    $out.net = $nets

    # ---------------- 声卡 ----------------
    $snd = @()
    foreach ($s in (Get-CimInstance Win32_SoundDevice)) {
        $snd += [PSCustomObject]@{ Name = $s.Name; Manufacturer = $s.Manufacturer; Status = $s.Status }
    }
    $out.sound = $snd

    # ---------------- 电池 ----------------
    $bat = @()
    foreach ($b in (Get-CimInstance Win32_Battery)) {
        $full = $null; $design = $null; $cycles = $null
        try {
            $fc = Get-CimInstance -Namespace root\wmi -ClassName BatteryFullChargedCapacity
            $ds = Get-CimInstance -Namespace root\wmi -ClassName BatteryStaticData
            $full = ($fc | Select-Object -First 1).FullChargedCapacity
            $design = ($ds | Select-Object -First 1).DesignedCapacity
            $cycles = ($ds | Select-Object -First 1).CycleCount
        } catch { }
        $bat += [PSCustomObject]@{
            Name = $b.Name; Status = "$($b.BatteryStatus)"; ChargePercent = $b.EstimatedChargeRemaining
            FullCapacityMWh = $full; DesignCapacityMWh = $design; CycleCount = $cycles
        }
    }
    $out.battery = $bat

    # ---------------- 温度 ----------------
    $temps = @()
    try {
        foreach ($t in (Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature)) {
            $temps += [PSCustomObject]@{
                Zone = $t.InstanceName
                Celsius = [math]::Round(($t.CurrentTemperature / 10) - 273.15, 1)
            }
        }
    } catch { }
    $out.temp = $temps

    $out | ConvertTo-Json -Depth 5 -Compress
    exit 0
}
