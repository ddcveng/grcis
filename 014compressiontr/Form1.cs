﻿using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Raster;

namespace _014compressiontr
{
  public partial class Form1 : Form
  {
    protected Bitmap inputImage = null;

    protected Bitmap outputImage = null;

    protected Bitmap diffImage = null;

    static readonly string rev = "$Rev$".Split( ' ' )[ 1 ];

    public Form1 ()
    {
      InitializeComponent();
      Text += " (rev: " + rev + ')';
    }

    private void buttonLoad_Click ( object sender, EventArgs e )
    {
      OpenFileDialog ofd = new OpenFileDialog();

      ofd.Title = "Open Image File";
      ofd.Filter = "Bitmap Files|*.bmp" +
          "|Gif Files|*.gif" +
          "|JPEG Files|*.jpg" +
          "|PNG Files|*.png" +
          "|TIFF Files|*.tif" +
          "|All image types|*.bmp;*.gif;*.jpg;*.png;*.tif";

      ofd.FilterIndex = 6;
      ofd.FileName = "";
      if ( ofd.ShowDialog() != DialogResult.OK )
        return;

      Image inp = Image.FromFile( ofd.FileName );
      if ( inputImage != null )
        inputImage.Dispose();
      inputImage = (Bitmap)inp;
      pictureBox1.Image = inputImage;

      if ( outputImage != null )
        outputImage.Dispose();
      if ( diffImage != null )
        diffImage.Dispose();
      outputImage =
      diffImage   = null;
    }

    private void buttonRecode_Click ( object sender, EventArgs e )
    {
      if ( inputImage == null ) return;
      Cursor.Current = Cursors.WaitCursor;

      Stopwatch sw = new Stopwatch();
      sw.Start();

      // 1. image encoding
      TRCodec codec = new TRCodec();
      FileStream fs = new FileStream( "code.bin", FileMode.Create );
      float quality = (float)numericQuality.Value;
      codec.EncodeImage( inputImage, fs, quality );

      // 2. code size
      fs.Flush();
      long fileSize = fs.Position;

      sw.Stop();
      labelElapsed.Text = String.Format( CultureInfo.InvariantCulture, "Enc: {0:f3}s, {1:f1}kb", 1.0e-3 * sw.ElapsedMilliseconds, (fileSize + 1023L) >> 10 );

      // 3. image decoding
      if ( outputImage != null )
        outputImage.Dispose();
      fs.Seek( 0L, SeekOrigin.Begin );
      outputImage = codec.DecodeImage( fs );
      fs.Close();

      // 5. comparison
      if ( diffImage != null )
        diffImage.Dispose();
      if ( outputImage != null )
      {
        diffImage = new Bitmap( inputImage.Width, inputImage.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb );
        float RMSE = Draw.ImageRMSE( inputImage, outputImage, diffImage );
        labelResult.Text = String.Format( CultureInfo.InvariantCulture, "RMSE: {0:f2}", RMSE );
        pictureBox1.Image = checkDiff.Checked ? diffImage : outputImage;
      }
      else
      {
        labelResult.Text = "File error";
        pictureBox1.Image = null;
        outputImage = diffImage = null;
      }

      Cursor.Current = Cursors.Default;
    }

    private void buttonSave_Click ( object sender, EventArgs e )
    {
      if ( outputImage == null ||
           diffImage == null ) return;

      SaveFileDialog sfd = new SaveFileDialog();
      sfd.Title = "Save PNG file";
      sfd.Filter = "PNG Files|*.png";
      sfd.AddExtension = true;
      sfd.FileName = "";
      if ( sfd.ShowDialog() != DialogResult.OK )
        return;

      if ( checkDiff.Checked )
        diffImage.Save( sfd.FileName, System.Drawing.Imaging.ImageFormat.Png );
      else
        outputImage.Save( sfd.FileName, System.Drawing.Imaging.ImageFormat.Png );
    }

    private void checkDiff_CheckedChanged ( object sender, EventArgs e )
    {
      pictureBox1.Image = checkDiff.Checked ? diffImage : outputImage;
    }
  }
}
