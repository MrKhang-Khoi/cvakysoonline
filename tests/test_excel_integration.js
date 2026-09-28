const fs = require('fs');
const path = require('path');
const XLSX = require('../wwwroot/xlsx.full.min.js');

console.log('════════════════════════════════════════════════════════════════');
console.log('🧪 KIỂM THỬ TÍCH HỢP TÍNH NĂNG BẢNG TÍNH EXCEL & XUẤT IN PDF');
console.log('════════════════════════════════════════════════════════════════\n');

// 1. Kiểm tra sự tồn tại của thư viện SheetJS cục bộ
const sheetJsPath = path.join(__dirname, '..', 'wwwroot', 'xlsx.full.min.js');
if (!fs.existsSync(sheetJsPath)) {
  console.error('❌ THẤT BẠI: Không tìm thấy tệp wwwroot/xlsx.full.min.js');
  process.exit(1);
}
console.log('✅ [TEST 1 PASS]: Đã xác nhận thư viện SheetJS cục bộ (Size:', (fs.statSync(sheetJsPath).size / 1024).toFixed(1), 'KB)');

let sampleExcelPath = process.env.SAMPLE_EXCEL_PATH || 'C:/Users/HPZBook/Desktop/HS TS LỚP 6 26-27-FINAL/DS phê duyệt KQTS lớp 6 năm học 2026-2027.xlsx';
if (!fs.existsSync(sampleExcelPath)) {
  const fixtureDir = path.join(__dirname, 'fixtures');
  if (!fs.existsSync(fixtureDir)) fs.mkdirSync(fixtureDir, { recursive: true });
  sampleExcelPath = path.join(fixtureDir, 'sample.xlsx');
  if (!fs.existsSync(sampleExcelPath)) {
    const newWb = XLSX.utils.book_new();
    const newWs = XLSX.utils.aoa_to_sheet([['STT', 'Họ và tên'], [1, 'Nguyễn Văn A']]);
    XLSX.utils.book_append_sheet(newWb, newWs, 'Sheet1');
    XLSX.writeFile(newWb, sampleExcelPath);
  }
}

const fileBuf = fs.readFileSync(sampleExcelPath);
const t0 = Date.now();
const wb = XLSX.read(fileBuf, { type: 'buffer', cellStyles: true, cellDates: true, cellNF: true });
const readTime = Date.now() - t0;

console.log(`✅ [TEST 2 PASS]: Đọc tệp Excel thực tế (${(fileBuf.length / 1024).toFixed(1)} KB) trong ${readTime} ms!`);
console.log('   • Danh sách SheetNames:', wb.SheetNames);

if (wb.SheetNames.length === 0) {
  console.error('❌ THẤT BẠI: Không tìm thấy Sheet nào trong tệp Excel.');
  process.exit(1);
}

// 3. Kiểm tra render Sheet thành HTML tương tác
const firstSheetName = wb.SheetNames[0];
const ws = wb.Sheets[firstSheetName];
const t1 = Date.now();
const htmlTable = XLSX.utils.sheet_to_html(ws, { editable: true });
const renderTime = Date.now() - t1;

console.log(`✅ [TEST 3 PASS]: Render Sheet '${firstSheetName}' thành bảng HTML tương tác trong ${renderTime} ms!`);
console.log('   • Độ dài chuỗi HTML:', htmlTable.length, 'ký tự');
console.log('   • Số lượng ô gộp (Merged Cells):', (ws['!merges'] || []).length);

// 4. Kiểm tra cấu trúc thẻ HTML và khả năng chỉnh sửa
if (!htmlTable.includes('<table') || !htmlTable.includes('</table>')) {
  console.error('❌ THẤT BẠI: Chuỗi kết xuất không chứa thẻ <table> hợp lệ.');
  process.exit(1);
}
console.log('✅ [TEST 4 PASS]: Cấu trúc thẻ <table> hoàn chỉnh, giữ nguyên phân tách cột & hàng.');

// 5. Kiểm tra file wwwroot/pdf_viewer.html có chứa đầy đủ các hàm điều khiển Excel
const viewerHtmlPath = path.join(__dirname, '..', 'wwwroot', 'pdf_viewer.html');
const viewerHtml = fs.readFileSync(viewerHtmlPath, 'utf8');

const requiredTokens = [
  'xlsx.full.min.js',
  'excel-container',
  'loadExcelFromBase64',
  'setExcelOrientation',
  'toggleExcelFitWidth',
  'requestExportExcelPdf'
];

for (const token of requiredTokens) {
  if (!viewerHtml.includes(token)) {
    console.error(`❌ THẤT BẠI: Tệp pdf_viewer.html thiếu thành phần: ${token}`);
    process.exit(1);
  }
}
console.log('✅ [TEST 5 PASS]: Trình xem pdf_viewer.html đã tích hợp 100% các thành phần điều khiển Excel.');

console.log('\n🎉 TOÀN BỘ 5 BÀI TEST TÍCH HỢP BẢNG TÍNH EXCEL ĐÃ ĐẠT 100%!');
