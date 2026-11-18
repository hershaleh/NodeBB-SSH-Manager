<Window x:Class="NodeBB.SSHManager.Views.DatabaseManagerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="מסד נתונים" Width="780" Height="850" MinWidth="660" MinHeight="720"
        WindowStartupLocation="CenterOwner" Background="#F4F6FA" FlowDirection="RightToLeft">
    <Grid Margin="24">
        <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
        <StackPanel>
            <TextBlock Text="מסד הנתונים של NodeBB" FontSize="24" FontWeight="Bold" Foreground="#182338"/>
            <TextBlock x:Name="InstallationLabel" Foreground="#778399" Margin="0,5,0,0"/>
        </StackPanel>
        <DockPanel Grid.Row="1" Margin="0,16,0,12">
            <Button x:Name="RefreshButton" Content="קריאת תצורה מחדש" Click="Refresh_Click" Padding="16,9" Background="#4263EB" Foreground="White" BorderThickness="0"/>
            <TextBlock x:Name="StatusText" DockPanel.Dock="Left" VerticalAlignment="Center" Foreground="#778399"/>
        </DockPanel>
        <Border Grid.Row="2" Background="White" BorderBrush="#E7EBF2" BorderThickness="1" CornerRadius="12" Padding="18">
            <StackPanel>
                <TextBlock Text="פרטי תצורה" FontWeight="SemiBold" FontSize="17" Margin="0,0,0,12"/>
                <Grid>
                    <Grid.ColumnDefinitions><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <TextBlock Text="סוג מסד נתונים" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="TypeText" Grid.Column="1" Margin="0,7" FontWeight="SemiBold"/>
                    <TextBlock Grid.Row="1" Text="שרת מארח" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="HostText" Grid.Row="1" Grid.Column="1" Margin="0,7"/>
                    <TextBlock Grid.Row="2" Text="יציאה" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="PortText" Grid.Row="2" Grid.Column="1" Margin="0,7"/>
                    <TextBlock Grid.Row="3" Text="שם מסד נתונים" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="DatabaseNameText" Grid.Row="3" Grid.Column="1" Margin="0,7"/>
                    <TextBlock Grid.Row="4" Text="שם משתמש מוגדר" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="UsernameText" Grid.Row="4" Grid.Column="1" Margin="0,7"/>
                    <TextBlock Grid.Row="5" Text="סיסמה מוגדרת" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="PasswordText" Grid.Row="5" Grid.Column="1" Margin="0,7"/>
                    <TextBlock Grid.Row="6" Text="כתובת URI מוגדרת" Foreground="#778399" Margin="0,7"/>
                    <TextBlock x:Name="UriText" Grid.Row="6" Grid.Column="1" Margin="0,7"/>
                </Grid>
            </StackPanel>
        </Border>
        <Border Grid.Row="3" Background="White" BorderBrush="#E7EBF2" BorderThickness="1" CornerRadius="12" Padding="14" Margin="0,14,0,0">
            <StackPanel>
                <TextBlock Text="גיבוי מסד נתונים" FontSize="16" FontWeight="SemiBold"/>
                <TextBlock Text="יצירת גיבוי MongoDB אמיתי באמצעות mongodump, עם פרטי החיבור מתוך config.json. קובץ הגיבוי יוצפן במחשב לפני שמירתו." TextWrapping="Wrap" Foreground="#778399" Margin="0,5,0,10"/>
                <TextBlock Text="סיסמת הצפנה (לפחות 12 תווים)" Foreground="#778399" FontSize="11"/>
                <PasswordBox x:Name="BackupPasswordBox" Padding="9" Margin="0,4,0,8"/>
                <TextBlock Text="אימות סיסמה" Foreground="#778399" FontSize="11"/>
                <PasswordBox x:Name="BackupPasswordConfirmBox" Padding="9" Margin="0,4,0,10"/>
                <Button x:Name="DatabaseBackupButton" Content="יצירת גיבוי MongoDB מוצפן" Click="DatabaseBackup_Click" HorizontalAlignment="Left" Padding="16,9" Background="#4263EB" Foreground="White" BorderThickness="0"/>
                <TextBlock Text="נדרשים mongodump ו-Node.js בשרת. קובץ הגיבוי הזמני נמחק לאחר ההצפנה." TextWrapping="Wrap" Foreground="#778399" FontSize="11" Margin="0,9,0,0"/><Separator Margin="0,14,0,12"/><TextBlock Text="שחזור מגיבוי מוצפן" FontSize="16" FontWeight="SemiBold"/><TextBlock Text="לפני שינוי הנתונים נוצר בשרת גיבוי חירום של מסד הנתונים הנוכחי. אם השחזור נכשל, המערכת מנסה להחזיר את המצב הקודם." TextWrapping="Wrap" Foreground="#778399" Margin="0,5,0,10"/><TextBlock Text="סיסמת הגיבוי" Foreground="#778399" FontSize="11"/><PasswordBox x:Name="RestorePasswordBox" Padding="9" Margin="0,4,0,10"/><Button x:Name="DatabaseRestoreButton" Content="בחירת גיבוי ושחזור MongoDB" Click="DatabaseRestore_Click" HorizontalAlignment="Left" Padding="16,9" Background="#A23B3B" Foreground="White" BorderThickness="0"/><TextBlock Text="פעולה זו משנה את הנתונים במסד היעד. יש לוודא שנבחר גיבוי השייך לשרת ולמסד הנכונים." TextWrapping="Wrap" Foreground="#9A5B13" FontSize="11" Margin="0,9,0,0"/>
            </StackPanel>
        </Border>
    </Grid>
</Window>
