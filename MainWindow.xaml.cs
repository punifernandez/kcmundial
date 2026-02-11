using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using KCMundial.Services;

namespace KCMundial
{
    public partial class MainWindow : Window
    {
        private KCMundialViewModel? _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            
            // Establecer título con BUILD tag inmediatamente
            this.Title = $"KC Mundial (BUILD: {App.BUILD_TAG}) - PHASE: {App.CURRENT_STARTUP_PHASE}";
            
            // Interceptar cierre de ventana
            this.Closing += MainWindow_Closing;
            this.Closed += MainWindow_Closed;
            
            // Phase0: NO crear ViewModel, NO registrar eventos, NO inicializar nada
            if (App.CURRENT_STARTUP_PHASE >= StartupPhase.Phase1_NoCamera)
            {
                CrashLogger.Log("MAINWINDOW_CONSTRUCTOR: Phase1+ - Creando ViewModel...");
                _viewModel = new KCMundialViewModel(App.CURRENT_STARTUP_PHASE);
                CrashLogger.Log("PHASE1: VM_CREATED");
                
                DataContext = _viewModel;
                CrashLogger.Log("PHASE1: VM_BOUND");

                // Phase3+: Registrar eventos de preview
                if (App.CURRENT_STARTUP_PHASE >= StartupPhase.Phase3_PreviewNoProcessing)
                {
                    // Usar nuevo evento RawPreviewFrameUpdated (solo RAW)
                    _viewModel.RawPreviewFrameUpdated += OnRawPreviewFrameUpdated;
                    
                    // Mantener compatibilidad con evento antiguo (deprecated)
                    #pragma warning disable CS0618 // Type or member is obsolete
                    _viewModel.PreviewFrameUpdated += OnPreviewFrameUpdated;
                    #pragma warning restore CS0618
                    
                    _viewModel.FlashRequested += OnFlashRequested;
                }

                Loaded += MainWindow_Loaded;
                Loaded += MainWindow_Loaded_Icons;
                ContentRendered += MainWindow_ContentRendered_Icons;
            }
            else
            {
                // Phase0: Solo ventana vacía, sin eventos
                CrashLogger.Log("MAINWINDOW_CONSTRUCTOR: Phase0 - Solo ventana, sin ViewModel");
            }
        }
        
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"CLOSE_PATH: MainWindow.Closing llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}, Cancel={e.Cancel}");
            CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
        }
        
        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"CLOSE_PATH: MainWindow.Closed llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}");
            CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
        }

        private void MainWindow_Loaded_Icons(object sender, RoutedEventArgs e)
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var iconsPath = Path.Combine(desktop, "KCMundial", "Assets", "icons");
                
                // Cargar ícono de impresora
                // Buscar en múltiples ubicaciones
                var possiblePrinterPaths = new List<string>();
                possiblePrinterPaths.Add(Path.Combine(iconsPath, "printer.png"));
                possiblePrinterPaths.Add(Path.Combine(desktop, "KCMundial", "Assets", "icons", "printer.png"));
                possiblePrinterPaths.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "printer.png"));
                
                string? printerIconPath = null;
                foreach (var path in possiblePrinterPaths)
                {
                    if (File.Exists(path))
                    {
                        printerIconPath = path;
                        System.Diagnostics.Debug.WriteLine($"✓ Ícono de impresora encontrado: {path}");
                        break;
                    }
                }
                
                if (printerIconPath != null)
                {
                    try
                    {
                        var printerImage = new BitmapImage();
                        printerImage.BeginInit();
                        printerImage.UriSource = new Uri(printerIconPath, UriKind.Absolute);
                        printerImage.DecodePixelWidth = 28;
                        printerImage.DecodePixelHeight = 28;
                        printerImage.CacheOption = BitmapCacheOption.OnLoad;
                        printerImage.EndInit();
                        printerImage.Freeze();
                        
                        // Usar directamente PrinterButton como con CameraButton
                        var printerIcon = FindVisualChild<Image>(PrinterButton, "PrinterIconImage");
                        if (printerIcon != null)
                        {
                            printerIcon.Source = printerImage;
                            System.Diagnostics.Debug.WriteLine("✓ Ícono de impresora asignado al botón");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ No se encontró PrinterIconImage. Intentando búsqueda alternativa...");
                            // Intentar buscar cualquier Image dentro del botón
                            var anyImage = FindVisualChild<Image>(PrinterButton);
                            if (anyImage != null)
                            {
                                anyImage.Source = printerImage;
                                System.Diagnostics.Debug.WriteLine("✓ Ícono de impresora asignado (búsqueda alternativa)");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("✗ No se encontró ningún Image dentro de PrinterButton");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Error al cargar ícono de impresora: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"✗ Ícono de impresora no encontrado. Buscado en: {string.Join(", ", possiblePrinterPaths)}");
                }
                
                // Cargar ícono de cámara
                var cameraIconPath = Path.Combine(iconsPath, "camera.png");
                if (File.Exists(cameraIconPath))
                {
                    var cameraImage = new BitmapImage();
                    cameraImage.BeginInit();
                    cameraImage.UriSource = new Uri(cameraIconPath, UriKind.Absolute);
                    cameraImage.DecodePixelWidth = 28;
                    cameraImage.DecodePixelHeight = 28;
                    cameraImage.CacheOption = BitmapCacheOption.OnLoad;
                    cameraImage.EndInit();
                    cameraImage.Freeze();
                    
                    var cameraIcon = FindVisualChild<Image>(CameraButton, "CameraIconImage");
                    if (cameraIcon != null)
                    {
                        cameraIcon.Source = cameraImage;
                    }
                }
                
                // Cargar ícono de cancelar
                var cancelIconPath = Path.Combine(iconsPath, "cancel.png");
                if (File.Exists(cancelIconPath))
                {
                    var cancelImage = new BitmapImage();
                    cancelImage.BeginInit();
                    cancelImage.UriSource = new Uri(cancelIconPath, UriKind.Absolute);
                    cancelImage.DecodePixelWidth = 28;
                    cancelImage.DecodePixelHeight = 28;
                    cancelImage.CacheOption = BitmapCacheOption.OnLoad;
                    cancelImage.EndInit();
                    cancelImage.Freeze();
                    
                    var cancelIcon = FindVisualChild<Image>(CloseButton, "CancelIconImage");
                    if (cancelIcon != null)
                    {
                        cancelIcon.Source = cancelImage;
                    }
                }
                
                // Cargar ícono de photography para el botón de inicio
                var photographyIconPath = Path.Combine(iconsPath, "photography.png");
                if (File.Exists(photographyIconPath))
                {
                    var photographyImage = new BitmapImage();
                    photographyImage.BeginInit();
                    photographyImage.UriSource = new Uri(photographyIconPath, UriKind.Absolute);
                    photographyImage.DecodePixelWidth = 120;
                    photographyImage.DecodePixelHeight = 120;
                    photographyImage.CacheOption = BitmapCacheOption.OnLoad;
                    photographyImage.EndInit();
                    photographyImage.Freeze();
                    
                    var startIcon = FindVisualChild<Image>(StartButton, "StartButtonIcon");
                    if (startIcon != null)
                    {
                        startIcon.Source = photographyImage;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar íconos: {ex.Message}");
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CrashLogger.Log($"MAINWINDOW_LOADED: Enter - Phase {App.CURRENT_STARTUP_PHASE}");
            
            try
            {
                // Phase0: NO hacer nada, solo loggear
                if (App.CURRENT_STARTUP_PHASE == StartupPhase.Phase0_OnlyWindow)
                {
                    CrashLogger.Log("MAINWINDOW_LOADED: Phase0 - Ventana cargada, sin inicialización");
                    return;
                }
                
                // Phase1+: Cargar y aplicar fuente
                if (App.CURRENT_STARTUP_PHASE >= StartupPhase.Phase1_NoCamera)
                {
                    LoadAndApplyFont();
                }
                
                // Phase2+: Inicializar ViewModel (pero sin preview)
                if (App.CURRENT_STARTUP_PHASE >= StartupPhase.Phase2_CameraNoPreview && _viewModel != null)
                {
                    try
                    {
                        CrashLogger.Log("MAINWINDOW_LOADED: Inicializando ViewModel (Phase2+)");
                        await _viewModel.InitializeAsync();
                        CrashLogger.Log("MAINWINDOW_LOADED: ViewModel inicializado");
                        
                        // Cargar impresoras disponibles (solo si fase >= Phase1)
                        if (App.CURRENT_STARTUP_PHASE >= StartupPhase.Phase1_NoCamera)
                        {
                            _viewModel.LoadAvailablePrinters();
                        }
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log("MAINWINDOW_LOADED: Error en InitializeAsync", ex);
                        System.Diagnostics.Debug.WriteLine($"Error en InitializeAsync: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                        // NO cerrar la app - mostrar error y dejar que el usuario intente de nuevo
                        MessageBox.Show($"Error al inicializar: {ex.Message}\n\nLa aplicación continuará pero puede no funcionar correctamente.", "Error de Inicialización", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else if (App.CURRENT_STARTUP_PHASE == StartupPhase.Phase1_NoCamera && _viewModel != null)
                {
                    // Phase1: Solo ViewModel básico, sin cámara
                    CrashLogger.Log("MAINWINDOW_LOADED: Phase1 - Inicializando servicios básicos (sin cámara)...");
                    try
                    {
                        await _viewModel.InitializeAsync();
                        CrashLogger.Log("PHASE1: READY");
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log("MAINWINDOW_LOADED: Phase1 - Error en InitializeAsync", ex);
                        throw; // Re-throw para no ocultar el error
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("MAINWINDOW_LOADED: ERROR CRÍTICO", ex);
                System.Diagnostics.Debug.WriteLine($"ERROR CRÍTICO en MainWindow_Loaded: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                MessageBox.Show($"Error crítico: {ex.Message}\n\nLa aplicación se cerrará.", "Error Crítico", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
            
            CrashLogger.Log($"MAINWINDOW_LOADED: Exit - Phase {App.CURRENT_STARTUP_PHASE}");
        }

        private void LoadAndApplyFont()
        {
            try
            {
                // Usar Bauhaus 93 para prueba
                FontFamily? fontFamily = null;
                
                // Intentar diferentes variaciones del nombre
                var possibleNames = new[] { "Bauhaus 93", "Bauhaus93", "Segoe UI" };
                
                foreach (var name in possibleNames)
                {
                    try
                    {
                        fontFamily = new FontFamily(name);
                        System.Diagnostics.Debug.WriteLine($"✓ Fuente encontrada con nombre: '{name}'");
                        break;
                    }
                    catch
                    {
                        continue;
                    }
                }
                
                if (fontFamily != null)
                {
                    // Esperar un momento para que la ventana esté completamente renderizada
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ApplyFontToElement(this, fontFamily);
                        System.Diagnostics.Debug.WriteLine($"✓ Fuente aplicada a todos los elementos");
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"✗ No se encontró la fuente 'The Seasons' instalada");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar y aplicar fuente: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
            }
        }

        private void ApplyFontToElement(DependencyObject element, FontFamily fontFamily)
        {
            if (element == null || fontFamily == null) return;
            
            try
            {
                // Aplicar a TextBlock
                if (element is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.FontFamily = fontFamily;
                    System.Diagnostics.Debug.WriteLine($"✓ Fuente aplicada a TextBlock: {textBlock.Text}");
                }
                // Aplicar a Button
                else if (element is System.Windows.Controls.Button button)
                {
                    button.FontFamily = fontFamily;
                    System.Diagnostics.Debug.WriteLine($"✓ Fuente aplicada a Button");
                }
                // Aplicar a otros controles de texto
                else if (element is System.Windows.Controls.Control control)
                {
                    control.FontFamily = fontFamily;
                }
                
                // Recursivamente aplicar a hijos
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                {
                    var child = VisualTreeHelper.GetChild(element, i);
                    ApplyFontToElement(child, fontFamily);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al aplicar fuente a elemento: {ex.Message}");
            }
        }

        private void MainWindow_ContentRendered_Icons(object? sender, EventArgs e)
        {
            // Aplicar ícono de impresora después de que el contenido esté completamente renderizado
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var possiblePrinterPaths = new List<string>
            {
                Path.Combine(desktop, "KCMundial", "Assets", "icons", "printer.png"),
                Path.Combine(desktop, "KCMundial", "Assets", "icons", "printer.png"),
                Path.Combine(AppContext.BaseDirectory, "Assets", "icons", "printer.png")
            };
            
            string? printerIconPath = null;
            foreach (var path in possiblePrinterPaths)
            {
                if (File.Exists(path))
                {
                    printerIconPath = path;
                    break;
                }
            }
            
            if (!string.IsNullOrEmpty(printerIconPath) && File.Exists(printerIconPath))
            {
                try
                {
                    var printerImage = new BitmapImage();
                    printerImage.BeginInit();
                    printerImage.UriSource = new Uri(printerIconPath, UriKind.Absolute);
                    printerImage.DecodePixelWidth = 28;
                    printerImage.DecodePixelHeight = 28;
                    printerImage.CacheOption = BitmapCacheOption.OnLoad;
                    printerImage.EndInit();
                    printerImage.Freeze();
                    
                    // Forzar actualización del layout
                    PrinterButton.UpdateLayout();
                    
                    // Buscar el Image dentro del ControlTemplate del botón
                    var printerIcon = FindVisualChild<Image>(PrinterButton, "PrinterIconImage");
                    if (printerIcon != null)
                    {
                        printerIcon.Source = printerImage;
                        System.Diagnostics.Debug.WriteLine("✓ Ícono de impresora asignado al botón (ContentRendered)");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("✗ No se encontró PrinterIconImage. Intentando búsqueda alternativa...");
                        // Intentar buscar cualquier Image dentro del botón
                        var anyImage = FindVisualChild<Image>(PrinterButton);
                        if (anyImage != null)
                        {
                            anyImage.Source = printerImage;
                            System.Diagnostics.Debug.WriteLine("✓ Ícono de impresora asignado (búsqueda alternativa)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ No se encontró ningún Image dentro de PrinterButton");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ Error al asignar ícono de impresora: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Handler para preview RAW (100% sin procesamiento)
        /// </summary>
        private void OnRawPreviewFrameUpdated(object? sender, BitmapSource frame)
        {
            // Ya estamos en UI thread desde Dispatcher.InvokeAsync
            if (frame != null)
            {
                PreviewImage.Source = frame;
                PreviewImage.Visibility = Visibility.Visible;
            }
        }
        
        /// <summary>
        /// Handler legacy para compatibilidad (deprecated)
        /// </summary>
        [Obsolete("Use OnRawPreviewFrameUpdated instead")]
        private void OnPreviewFrameUpdated(object? sender, BitmapSource frame)
        {
            // Delegar al nuevo handler
            OnRawPreviewFrameUpdated(sender, frame);
        }

        private void OnFlashRequested(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(async () =>
            {
                FlashOverlay.Visibility = Visibility.Visible;
                FlashOverlay.Opacity = 1.0;
                await Task.Delay(100);
                FlashOverlay.Opacity = 0;
                FlashOverlay.Visibility = Visibility.Collapsed;
            });
        }

        private void PreviewOverlay_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateHeadGuidePosition();
        }

        private void PreviewOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHeadGuidePosition();
        }

        private void UpdateHeadGuidePosition()
        {
            if (PreviewOverlay == null || HeadGuide == null || HeadGuideText == null)
                return;

            try
            {
                // Obtener el tamaño del Canvas
                double canvasWidth = PreviewOverlay.ActualWidth;
                double canvasHeight = PreviewOverlay.ActualHeight;

                if (canvasWidth <= 0 || canvasHeight <= 0)
                    return;

                // Tamaño del rectángulo guía (cabeza y hombros) - Más grande para mejor visibilidad
                double guideWidth = 500;
                double guideHeight = 650;

                // Posicionar el rectángulo: centrado horizontalmente, en la parte superior (10% desde arriba)
                double guideX = (canvasWidth - guideWidth) / 2;
                double guideY = canvasHeight * 0.10; // 10% desde arriba

                Canvas.SetLeft(HeadGuide, guideX);
                Canvas.SetTop(HeadGuide, guideY);
                
                // Asegurar que el rectángulo sea visible
                HeadGuide.Width = guideWidth;
                HeadGuide.Height = guideHeight;

                // Forzar medida del texto si aún no se ha medido
                HeadGuideText.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                HeadGuideText.Arrange(new System.Windows.Rect(HeadGuideText.DesiredSize));

                // Posicionar el texto debajo del círculo, centrado
                double textWidth = HeadGuideText.ActualWidth > 0 ? HeadGuideText.ActualWidth : HeadGuideText.DesiredSize.Width;
                if (textWidth <= 0)
                {
                    textWidth = 300; // Estimado si aún no tiene tamaño
                }

                double textX = (canvasWidth - textWidth) / 2;
                double textY = guideY + guideHeight + 15; // 15px de separación

                Canvas.SetLeft(HeadGuideText, textX);
                Canvas.SetTop(HeadGuideText, textY);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al actualizar posición de guía: {ex.Message}");
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_viewModel?.State == KCMundialState.Idle || _viewModel?.State == KCMundialState.Error)
                {
                    var stackTrace = Environment.StackTrace;
                    CrashLogger.Log($"CLOSE_PATH: Window_KeyDown (Escape) - Cerrando ventana");
                    CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
                    Close();
                }
                else
                {
                    _viewModel?.Cancel();
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var stackTrace = Environment.StackTrace;
                CrashLogger.Log($"CLOSE_PATH: CloseButton_Click - Cerrando ventana");
                CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
                
                _viewModel?.Cancel();
                Close();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CLOSE_PATH: Error en CloseButton_Click, llamando Shutdown", ex);
                Application.Current.Shutdown();
            }
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle camera popup
            if (CameraPopup.Visibility == Visibility.Visible)
            {
                CameraPopup.Visibility = Visibility.Collapsed;
            }
            else
            {
                CameraPopup.Visibility = Visibility.Visible;
            }
        }

        private void PrinterButton_Click(object sender, RoutedEventArgs e)
        {
            // Toggle del popup de impresoras
            if (PrinterPopup.Visibility == Visibility.Visible)
            {
                PrinterPopup.Visibility = Visibility.Collapsed;
            }
            else
            {
                PrinterPopup.Visibility = Visibility.Visible;
                CameraPopup.Visibility = Visibility.Collapsed; // Cerrar popup de cámaras si está abierto
            }
        }

        private void PrinterListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Cuando se selecciona una impresora, cerrar el popup
            if (PrinterListBox.SelectedItem != null)
            {
                PrinterPopup.Visibility = Visibility.Collapsed;
            }
        }

        private void CameraListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Cuando se selecciona una cámara, cambiar y cerrar el popup
            if (CameraListBox.SelectedItem != null)
            {
                CameraPopup.Visibility = Visibility.Collapsed;
                PrinterPopup.Visibility = Visibility.Collapsed; // Cerrar popup de impresoras si está abierto
            }
        }

        // Métodos de selección de diseños y frames eliminados - ya no se necesitan
        
        public void UpdateDesignSelection()
        {
            try
            {
                if (_viewModel?.AvailableDesigns == null) return;

                // Buscar el ItemsControl de diseños
                var idleScreen = FindVisualChild<Border>(this, "IdleScreen");
                if (idleScreen == null) return;

                var designsControl = FindVisualChild<ItemsControl>(idleScreen, "DesignsItemsControl");
                if (designsControl == null) return;

                var selectedDesign = _viewModel.SelectedDesign;
                if (selectedDesign == null) return;

                // Esperar a que los contenedores estén generados
                designsControl.UpdateLayout();

                // Recorrer los items y actualizar el borde
                foreach (var item in designsControl.Items)
                {
                    var container = designsControl.ItemContainerGenerator.ContainerFromItem(item);
                    if (container == null) continue;

                    var border = FindVisualChild<Border>(container);
                    if (border != null)
                    {
                        if (item is DesignInfo designInfo && designInfo.DesignType == selectedDesign.DesignType)
                        {
                            border.BorderBrush = new SolidColorBrush(Colors.Yellow);
                            border.BorderThickness = new Thickness(3);
                            border.Margin = new Thickness(0); // Sin margen para que el borde no se corte
                            border.Padding = new Thickness(2); // Mantener padding interno
                        }
                        else
                        {
                            border.BorderBrush = new SolidColorBrush(Colors.Transparent);
                            border.BorderThickness = new Thickness(1);
                            border.Margin = new Thickness(2);
                            border.Padding = new Thickness(2);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en UpdateDesignSelection: {ex.Message}");
            }
        }

        public void UpdateFrameSelection()
        {
            try
            {
                // Buscar el ItemsControl de frames en el IdleScreen
                var idleScreen = FindVisualChild<Border>(this, "IdleScreen");
                if (idleScreen == null) return;

                // Buscar todos los ScrollViewers dentro del IdleScreen
                var scrollViewers = new List<ScrollViewer>();
                FindVisualChildren<ScrollViewer>(idleScreen, scrollViewers);
                
                // El segundo ScrollViewer debería ser el de frames (el primero es de diseños)
                ScrollViewer? framesScrollViewer = null;
                if (scrollViewers.Count > 1)
                {
                    framesScrollViewer = scrollViewers[1];
                }
                else if (scrollViewers.Count == 1)
                {
                    // Si solo hay uno, verificar que no sea el de diseños
                    var designsControl = FindVisualChild<ItemsControl>(scrollViewers[0], "DesignsItemsControl");
                    if (designsControl == null)
                    {
                        framesScrollViewer = scrollViewers[0];
                    }
                }

                if (framesScrollViewer != null)
                {
                    var itemsControl = FindVisualChild<ItemsControl>(framesScrollViewer);
                    if (itemsControl != null)
                    {
                        // Esperar a que los contenedores estén generados
                        itemsControl.UpdateLayout();

                        foreach (var item in itemsControl.Items)
                        {
                            var container = itemsControl.ItemContainerGenerator.ContainerFromItem(item);
                            if (container != null)
                            {
                                var border = FindVisualChild<Border>(container);
                                if (border != null)
                                {
                                    if (item is FrameInfo frame && frame.FileName == _viewModel.SelectedFrame?.FileName)
                                    {
                                        border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 255, 0)); // Amarillo
                                        border.BorderThickness = new Thickness(4);
                                        border.Margin = new Thickness(0); // Sin margen para que el borde no se corte
                                        border.Padding = new Thickness(0); // Sin padding adicional
                                    }
                                    else
                                    {
                                        border.BorderBrush = Brushes.Transparent;
                                        border.BorderThickness = new Thickness(1);
                                        border.Margin = new Thickness(3);
                                        border.Padding = new Thickness(0);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en UpdateFrameSelection: {ex.Message}");
            }
        }
        
        private void FindVisualChildren<T>(DependencyObject depObj, System.Collections.Generic.List<T> children) where T : DependencyObject
        {
            if (depObj == null) return;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                
                if (child is T t)
                {
                    children.Add(t);
                }
                
                FindVisualChildren<T>(child, children);
            }
        }
        
        private static T? FindVisualChild<T>(DependencyObject depObj, string? name = null) where T : DependencyObject
        {
            if (depObj == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                
                if (child is FrameworkElement fe && !string.IsNullOrEmpty(name))
                {
                    if (fe.Name == name && child is T t)
                        return t;
                }
                else if (string.IsNullOrEmpty(name) && child is T t)
                {
                    return t;
                }
                
                var childItem = FindVisualChild<T>(child, name);
                if (childItem != null)
                    return childItem;
            }
            return null;
        }
        
        private static T? FindVisualChild<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) return null;
            
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T t)
                    return t;
                
                var childItem = FindVisualChild<T>(child);
                if (childItem != null)
                    return childItem;
            }
            return null;
        }
    }
}

