using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace AdultZone.Desktop;

/// <summary>Adult Zone's dark cinema palette: ink black, graphite panels, the ember accent.</summary>
public static class Theme
{
    public static readonly Color InkC = C("#08080B");
    public static readonly Color InkSoftC = C("#0E0E13");
    public static readonly Color PanelC = C("#15151C");
    public static readonly Color Panel2C = C("#1E1E27");
    public static readonly Color Panel3C = C("#2A2A35");
    public static readonly Color EmberC = C("#FF8A3D");
    public static readonly Color EmberHiC = C("#FF9C57");
    public static readonly Color EmberDimC = C("#B95E24");
    public static readonly Color TextC = C("#F3F2F6");
    public static readonly Color MutedC = C("#8D8B99");
    public static readonly Color FaintC = C("#5E5C69");
    public static readonly Color HdC = C("#52D0A0");
    public static readonly Color SubsiteC = C("#7FB4FF");
    public static readonly Color WarnC = C("#FF6B6B");

    public static readonly Brush Ink = B(InkC), InkSoft = B(InkSoftC), Panel = B(PanelC), Panel2 = B(Panel2C), Panel3 = B(Panel3C),
        Ember = B(EmberC), EmberHi = B(EmberHiC), EmberDim = B(EmberDimC), Text = B(TextC), Muted = B(MutedC), Faint = B(FaintC),
        Hd = B(HdC), Subsite = B(SubsiteC), Warn = B(WarnC),
        OnEmber = B(C("#160B03")),
        Line = B(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)),
        LineSoft = B(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
        Glass = B(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)),
        GlassHi = B(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
        EmberWash = B(Color.FromArgb(0x21, 0xFF, 0x8A, 0x3D)),
        Body = B(C("#C7C5D0")),
        Clear = Brushes.Transparent,
        HitTarget = B(Color.FromArgb(0x01, 0, 0, 0));

    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static Brush B(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Brush Alpha(Color c, byte a)
    {
        var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static void Apply(Application app)
    {
        try
        {
            var dictionary = (ResourceDictionary)XamlReader.Parse(Xaml);
            app.Resources.MergedDictionaries.Add(dictionary);
        }
        catch (Exception ex)
        {
            App.Log("Theme failed to load, using default controls: " + ex);
        }
    }

    public static Style? Style(string key) =>
        Application.Current?.TryFindResource(key) as Style;

    const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

  <Style x:Key="EmberFocus">
    <Setter Property="Control.Template">
      <Setter.Value>
        <ControlTemplate>
          <Rectangle Stroke="#FF9C57" StrokeThickness="2" RadiusX="6" RadiusY="6" Margin="-3" SnapsToDevicePixels="True"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- .btn -->
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="Background" Value="#1E1E27"/>
    <Setter Property="BorderThickness" Value="0"/>
    <Setter Property="Padding" Value="20,10"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="FontWeight" Value="SemiBold"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource EmberFocus}"/>
    <Setter Property="HorizontalContentAlignment" Value="Center"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}"
                  SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="Center" RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#2A2A35"/>
              <Setter TargetName="bd" Property="RenderTransform">
                <Setter.Value><TranslateTransform Y="-1"/></Setter.Value>
              </Setter>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ember button -->
  <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="#FF8A3D"/>
    <Setter Property="Foreground" Value="#160B03"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}"
                  SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"
                              RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#FF9C57"/>
              <Setter TargetName="bd" Property="RenderTransform">
                <Setter.Value><TranslateTransform Y="-1"/></Setter.Value>
              </Setter>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- ghost button -->
  <Style x:Key="Ghost" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
    <Setter Property="Background" Value="#1AFFFFFF"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="bd" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}"
                  SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"
                              RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#33FFFFFF"/>
              <Setter TargetName="bd" Property="RenderTransform">
                <Setter.Value><TranslateTransform Y="-1"/></Setter.Value>
              </Setter>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- a button that is just its content -->
  <Style x:Key="Bare" TargetType="Button">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource EmberFocus}"/>
    <Setter Property="Padding" Value="0"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="VerticalContentAlignment" Value="Stretch"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" BorderThickness="0">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.3"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ToolTip">
    <Setter Property="Background" Value="#101016"/>
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="BorderBrush" Value="#2A2A35"/>
    <Setter Property="Padding" Value="8,5"/>
  </Style>

  <!-- .input -->
  <Style TargetType="TextBox">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="Background" Value="#0E0E13"/>
    <Setter Property="BorderBrush" Value="#17FFFFFF"/>
    <Setter Property="CaretBrush" Value="#FF9C57"/>
    <Setter Property="SelectionBrush" Value="#FF8A3D"/>
    <Setter Property="Padding" Value="12,9"/>
    <Setter Property="FontSize" Value="13.5"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="bd" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="1" CornerRadius="8">
            <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                          VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True">
              <Setter TargetName="bd" Property="BorderBrush" Value="#FF8A3D"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.5"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- .select -->
  <Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="Padding" Value="12,7"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="bd" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="5">
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#17FFFFFF"/>
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
              <Setter Property="Foreground" Value="#FF9C57"/>
              <Setter Property="FontWeight" Value="Bold"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType="ComboBox">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Background" Value="#1E1E27"/>
    <Setter Property="BorderBrush" Value="#17FFFFFF"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="12,0,32,0"/>
    <Setter Property="MinWidth" Value="120"/>
    <Setter Property="Height" Value="38"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource EmberFocus}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid>
            <ToggleButton x:Name="tb" Focusable="False" ClickMode="Press" Background="{TemplateBinding Background}"
                          BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border x:Name="b" CornerRadius="6"
                          Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="{TemplateBinding BorderThickness}">
                    <Path HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,13,0"
                          Data="M0,0 L4.5,4.5 L9,0" Stroke="#8D8B99" StrokeThickness="1.8"
                          StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                      <Setter TargetName="b" Property="BorderBrush" Value="#FF8A3D"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                      <Setter TargetName="b" Property="BorderBrush" Value="#FF8A3D"/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter IsHitTestVisible="False" Margin="{TemplateBinding Padding}" VerticalAlignment="Center"
                              Content="{TemplateBinding SelectionBoxItem}"
                              ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                   IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}">
              <Border Background="#101016" BorderBrush="#2A2A35" BorderThickness="1" CornerRadius="9" Padding="5"
                      MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                      MaxHeight="{TemplateBinding MaxDropDownHeight}" Margin="0,4,0,0">
                <ScrollViewer>
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- .switch -->
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="FontSize" Value="14"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource EmberFocus}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <Border x:Name="track" Width="42" Height="24" CornerRadius="12" Background="#2A2A35" VerticalAlignment="Center">
              <Ellipse x:Name="knob" Width="18" Height="18" Fill="#ffffff" HorizontalAlignment="Left" Margin="3,0,0,0"/>
            </Border>
            <ContentPresenter Margin="12,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="False"/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="track" Property="Background" Value="#FF8A3D"/>
              <Setter TargetName="knob" Property="HorizontalAlignment" Value="Right"/>
              <Setter TargetName="knob" Property="Margin" Value="0,0,3,0"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- scroll bars -->
  <ControlTemplate x:Key="VBar" TargetType="ScrollBar">
    <Track x:Name="PART_Track" IsDirectionReversed="True">
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Border x:Name="t" Background="#2A2A35" CornerRadius="5" Margin="2"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="t" Property="Background" Value="#B95E24"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </ControlTemplate>
  <ControlTemplate x:Key="HBar" TargetType="ScrollBar">
    <Track x:Name="PART_Track">
      <Track.Thumb>
        <Thumb>
          <Thumb.Template>
            <ControlTemplate TargetType="Thumb">
              <Border x:Name="t" Background="#2A2A35" CornerRadius="5" Margin="2"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="t" Property="Background" Value="#B95E24"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Thumb.Template>
        </Thumb>
      </Track.Thumb>
    </Track>
  </ControlTemplate>
  <Style TargetType="ScrollBar">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Width" Value="10"/>
    <Setter Property="MinWidth" Value="10"/>
    <Setter Property="Template" Value="{StaticResource VBar}"/>
    <Style.Triggers>
      <Trigger Property="Orientation" Value="Horizontal">
        <Setter Property="Width" Value="Auto"/>
        <Setter Property="MinWidth" Value="0"/>
        <Setter Property="Height" Value="10"/>
        <Setter Property="MinHeight" Value="10"/>
        <Setter Property="Template" Value="{StaticResource HBar}"/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <!-- seek bar and volume -->
  <Style x:Key="TrackButton" TargetType="RepeatButton">
    <Setter Property="Focusable" Value="False"/>
    <Setter Property="IsTabStop" Value="False"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="RepeatButton">
          <Border Background="Transparent">
            <Border Height="4" CornerRadius="2" Background="{TemplateBinding Background}" VerticalAlignment="Center"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="Slider">
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FocusVisualStyle" Value="{StaticResource EmberFocus}"/>
    <Setter Property="IsMoveToPointEnabled" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Slider">
          <Grid Background="Transparent" MinHeight="18">
            <Track x:Name="PART_Track">
              <Track.DecreaseRepeatButton>
                <RepeatButton Style="{StaticResource TrackButton}" Background="#FF8A3D" Command="{x:Static Slider.DecreaseLarge}"/>
              </Track.DecreaseRepeatButton>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style="{StaticResource TrackButton}" Background="#40FFFFFF" Command="{x:Static Slider.IncreaseLarge}"/>
              </Track.IncreaseRepeatButton>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType="Thumb">
                      <Ellipse Width="13" Height="13" Fill="#FF9C57"/>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- right-click menus -->
  <Style TargetType="ContextMenu">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="OverridesDefaultStyle" Value="True"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Background="#101016" BorderBrush="#2A2A35" BorderThickness="1" CornerRadius="9" Padding="5">
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="MenuItem">
    <Setter Property="Foreground" Value="#F3F2F6"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="bd" Background="Transparent" CornerRadius="6" Padding="12,8,20,8" MinWidth="170">
            <ContentPresenter ContentSource="Header" RecognizesAccessKey="False"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="bd" Property="Background" Value="#17FFFFFF"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

</ResourceDictionary>
""";
}
