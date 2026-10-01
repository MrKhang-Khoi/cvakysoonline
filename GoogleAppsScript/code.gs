/**
 * =========================================================================================
 * DỰ ÁN KÝ SỐ LIÊN THÔNG GIÁO DỤC (VGCA ONLINE SIGN TOOL PRO)
 * GOOGLE APPS SCRIPT WEB APP GATEWAY - LƯU TRỮ VÀ QUẢN LÝ BÁO CÁO TRÊN GOOGLE DRIVE
 * =========================================================================================
 * 
 * HƯỚNG DẪN TRIỂN KHAI NHANH (DEPLOY TRONG 2 PHÚT):
 * 1. Truy cập https://script.google.com bằng tài khoản Google (tài khoản trường học hoặc cá nhân).
 * 2. Bấm "Dự án mới" (New Project), đổi tên thành: "VGCA_Drive_Gateway".
 * 3. Xóa hết code mặc định, sao chép toàn bộ nội dung file này dán vào.
 * 4. Bấm "Triển khai" (Deploy) -> "Tùy chọn triển khai mới" (New deployment).
 * 5. Chọn loại: "Ứng dụng web" (Web app).
 *    - Mô tả: "VGCA Storage Gateway v1.0"
 *    - Thực thi dưới dạng (Execute as): "Tôi" (Me - tài khoản Google của bạn)
 *    - Ai có quyền truy cập (Who has access): "Bất kỳ ai" (Anyone)
 * 6. Bấm "Triển khai" (Deploy) -> Cấp quyền truy cập Google Drive khi được hỏi.
 * 7. Sao chép "URL ứng dụng web" (Web App URL) có dạng:
 *    https://script.google.com/macros/s/AKfycb.../exec
 * 8. Dán URL này vào file workflow_config.json trong phần mềm VGCA SignToolPro:
 *    "googleAppsScriptUrl": "https://script.google.com/macros/s/AKfycb.../exec"
 * =========================================================================================
 */

// Tên thư mục lưu trữ mặc định trên Google Drive của bạn
const DEFAULT_FOLDER_NAME = "VGCA_BaoCao_KySo_LienThong";

// Khóa bảo mật tùy chọn (để trống nếu không yêu cầu khóa bí mật)
const API_SECRET_KEY = ""; 

/**
 * Xử lý yêu cầu GET: Kiểm tra trạng thái hoạt động của Gateway (Ping / Health Check)
 */
function doGet(e) {
  const response = {
    success: true,
    service: "VGCA Google Drive Storage Gateway",
    status: "ONLINE",
    timestamp: new Date().toISOString(),
    message: "Cổng kết nối Google Drive của VGCA SignToolPro đang hoạt động bình thường!"
  };
  
  return ContentService.createTextOutput(JSON.stringify(response))
    .setMimeType(ContentService.MimeType.JSON);
}

/**
 * Xử lý yêu cầu POST: Tiếp nhận tệp PDF và lưu trữ vào Google Drive
 */
function doPost(e) {
  try {
    if (!e || !e.postData || !e.postData.contents) {
      return jsonError("Không nhận được dữ liệu tải lên (Empty postData).", 400);
    }

    let requestData;
    try {
      requestData = JSON.parse(e.postData.contents);
    } catch (parseEx) {
      return jsonError("Định dạng dữ liệu không hợp lệ. Yêu cầu JSON: " + parseEx.toString(), 400);
    }

    // 1. Kiểm tra khóa bảo mật nếu được cấu hình
    if (API_SECRET_KEY && API_SECRET_KEY.trim() !== "") {
      if (requestData.apiKey !== API_SECRET_KEY) {
        return jsonError("Khóa bảo mật (apiKey) không chính xác. Từ chối truy cập.", 403);
      }
    }

    const action = requestData.action || "uploadFile";

    // 2. Định tuyến theo hành động
    if (action === "ping") {
      return jsonSuccess({
        status: "PONG",
        timestamp: new Date().toISOString()
      });
    }

    if (action === "uploadFile") {
      return handleUploadFile(requestData);
    }

    return jsonError("Hành động '" + action + "' không được hỗ trợ.", 400);

  } catch (globalEx) {
    return jsonError("Lỗi hệ thống máy chủ Google Apps Script: " + globalEx.toString(), 500);
  }
}

/**
 * Hàm lưu tệp PDF lên Google Drive và cấp quyền xem
 */
function handleUploadFile(data) {
  const fileName = data.fileName || ("BaoCao_VGCA_" + Utilities.formatDate(new Date(), "GMT+7", "yyyyMMdd_HHmmss") + ".pdf");
  const base64Data = data.base64Data;
  const targetFolderName = data.folderName || DEFAULT_FOLDER_NAME;
  const customFolderId = data.folderId;
  const reportId = data.reportId || "";

  if (!base64Data) {
    return jsonError("Thiếu dữ liệu tệp (base64Data is required).", 400);
  }

  // 1. Tìm hoặc tạo thư mục lưu trữ trên Google Drive
  let targetFolder;
  if (customFolderId && customFolderId.trim() !== "") {
    try {
      targetFolder = DriveApp.getFolderById(customFolderId);
    } catch (fEx) {
      targetFolder = getOrCreateFolder(targetFolderName);
    }
  } else {
    targetFolder = getOrCreateFolder(targetFolderName);
  }

  // 2. Giải mã Base64 thành Blob PDF nhị phân
  const fileBytes = Utilities.base64Decode(base64Data);
  const pdfBlob = Utilities.newBlob(fileBytes, "application/pdf", fileName);

  // 3. Tạo tệp mới trên Google Drive
  const createdFile = targetFolder.createFile(pdfBlob);

  // 4. Thiết lập mô tả tệp kèm mã báo cáo nếu có
  if (reportId) {
    createdFile.setDescription("Báo cáo ký số liên thông VGCA - Mã: " + reportId + " - Thời gian tạo: " + Utilities.formatDate(new Date(), "GMT+7", "dd/MM/yyyy HH:mm:ss"));
  }

  // 5. Cấp quyền chia sẻ: Bất kỳ ai có đường link đều có thể xem (Phù hợp chia sẻ báo cáo giáo dục)
  try {
    createdFile.setSharing(DriveApp.Access.ANYONE_WITH_LINK, DriveApp.Permission.VIEW);
  } catch (permEx) {
    // Trường hợp tài khoản tổ chức khóa quyền chia sẻ công khai ngoài domain
    Logger.log("Không thể gán quyền ANYONE_WITH_LINK: " + permEx.toString());
  }

  const fileId = createdFile.getId();
  const viewUrl = createdFile.getUrl();
  const directViewUrl = "https://drive.google.com/file/d/" + fileId + "/view?usp=sharing";
  const downloadUrl = "https://drive.google.com/uc?export=download&id=" + fileId;

  return jsonSuccess({
    fileId: fileId,
    fileName: createdFile.getName(),
    fileSize: createdFile.getSize(),
    folderId: targetFolder.getId(),
    folderName: targetFolder.getName(),
    viewUrl: viewUrl,
    directViewUrl: directViewUrl,
    downloadUrl: downloadUrl,
    createdAt: new Date().toISOString()
  });
}

/**
 * Tìm thư mục theo tên, nếu chưa có thì tự động tạo mới ở thư mục gốc
 */
function getOrCreateFolder(folderName) {
  const folders = DriveApp.getFoldersByName(folderName);
  if (folders.hasNext()) {
    return folders.next();
  }
  return DriveApp.createFolder(folderName);
}

/**
 * Trả về phản hồi JSON thành công
 */
function jsonSuccess(payload) {
  const output = {
    success: true,
    data: payload
  };
  return ContentService.createTextOutput(JSON.stringify(output))
    .setMimeType(ContentService.MimeType.JSON);
}

/**
 * Trả về phản hồi JSON lỗi
 */
function jsonError(message, code) {
  const output = {
    success: false,
    code: code || 400,
    message: message
  };
  return ContentService.createTextOutput(JSON.stringify(output))
    .setMimeType(ContentService.MimeType.JSON);
}
