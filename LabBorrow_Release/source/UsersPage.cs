// =====================================================================
//  UsersPage.cs  --  借用人管理（学生卡 / 借用人卡）
//  ---------------------------------------------------------------------
//  数据表 users：card_epc(卡号) / name(姓名) / user_no(学号) / class_name(班级)
//                role(角色) / notes(备注)
//  配合「借用人卡模式」使用：先刷学生卡 → 再刷设备卡 → 自动完成借出，
//  不必手工输入姓名；未开启该模式时，刷卡也会自动填入借出对话框。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace LabBorrow
{
    /// <summary>角色中文标签 / 数据库码值互转</summary>
    internal static class Roles
    {
        public static readonly string[] Labels = { "学生", "教师", "管理员" };

        public static string Code(string label)
        {
            if (label == "教师") return "teacher";
            if (label == "管理员") return "admin";
            return "student";
        }

        public static string Label(string code)
        {
            if (code == "teacher") return "教师";
            if (code == "admin") return "管理员";
            return "学生";
        }
    }

    public partial class MainForm
    {
        // ---- 借用人页控件 ----
        private TextBox txtUserSearch;
        private DataGridView dgvUsers;

        // ===================================================================
        //  借用人页
        // ===================================================================
        private Control MakeToolbarUsers()
        {
            var bar = new Panel
            {
                BackColor = Th.BgCard,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12, 10, 12, 10),
                MinimumSize = new Size(0, 56)
            };
            bar.Paint += PaintCardBorder;
            RoundRegion(bar, 8);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent
            };

            var lbl = new Label { Text = "搜索：", AutoSize = true, ForeColor = Th.TxtSec, Font = Th.NormFont, Margin = new Padding(0, 8, 4, 0) };
            txtUserSearch = new TextBox
            {
                Width = 200, BackColor = Th.BgInput, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.FixedSingle, Font = Th.NormFont, Margin = new Padding(0, 4, 12, 0)
            };
            txtUserSearch.TextChanged += (s, e) => ReloadUsers();

            var btnAdd = MakeButton("+ 新增借用人", Th.Accent, Point.Empty, new Size(120, 30));
            btnAdd.Margin = new Padding(0, 3, 10, 0);
            btnAdd.Click += (s, e) => BtnUserAdd_Click(null, EventArgs.Empty);

            var btnEdit = MakeButton("编辑", Th.Accent2, Point.Empty, new Size(80, 30));
            btnEdit.Margin = new Padding(0, 3, 10, 0);
            btnEdit.Click += (s, e) => BtnUserEdit_Click(null, EventArgs.Empty);

            var btnDel = MakeButton("删除", Th.Danger, Point.Empty, new Size(80, 30));
            btnDel.Margin = new Padding(0, 3, 10, 0);
            btnDel.Click += (s, e) => BtnUserDel_Click(null, EventArgs.Empty);

            var btnCard = MakeButton("读取卡号录入", Th.Success, Point.Empty, new Size(130, 30));
            btnCard.Margin = new Padding(0, 3, 0, 0);
            btnCard.Click += (s, e) => BtnUserReadCard_Click(null, EventArgs.Empty);

            flow.Controls.AddRange(new Control[] { lbl, txtUserSearch, btnAdd, btnEdit, btnDel, btnCard });
            bar.Controls.Add(flow);
            return bar;
        }

        private void BuildUsersPage()
        {
            dgvUsers = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = Th.BgPanel, ForeColor = Th.TxtPri,
                BorderStyle = BorderStyle.None, GridColor = Th.Border, RowHeadersVisible = false,
                AllowUserToAddRows = false, ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Font = Th.NormFont, EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 36, RowTemplate = { Height = 32 }, AutoGenerateColumns = false
            };
            dgvUsers.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Th.BgPanel, ForeColor = Th.TxtSec, Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0)
            };
            dgvUsers.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Th.BgCard, ForeColor = Th.TxtPri,
                SelectionBackColor = Th.Accent, SelectionForeColor = Color.White, Padding = new Padding(8, 0, 0, 0)
            };
            dgvUsers.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Th.BgPanel };
            EnableDoubleBuffer(dgvUsers);

            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "卡号(EPC)", Name = "Card", Width = 230 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "姓名", Name = "Name", Width = 120 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "学号/工号", Name = "No", Width = 120 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "班级/部门", Name = "Class", Width = 140 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "手机号", Name = "Phone", Width = 120 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "邮箱", Name = "Email", Width = 180 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "角色", Name = "Role", Width = 80 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "在借", Name = "Lent", Width = 60 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", Name = "Notes", Width = 180 });
            dgvUsers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "更新时间", Name = "Updated", Width = 150 });

            foreach (DataGridViewColumn col in dgvUsers.Columns)
            {
                col.FillWeight = Math.Max(30, col.Width);
                col.MinimumWidth = 50;
            }
            dgvUsers.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            dgvUsers.Columns["Card"].DefaultCellStyle.Font = Th.MonoFont;
            dgvUsers.Columns["Card"].DefaultCellStyle.ForeColor = Th.TxtSec;
            dgvUsers.CellFormatting += (s, e) =>
            {
                if (e.Value == null || dgvUsers.Columns[e.ColumnIndex].Name != "Role") return;
                string v = e.Value.ToString();
                e.CellStyle.Font = Th.BoldFont;
                if (v == "教师") e.CellStyle.ForeColor = Th.Accent;
                else if (v == "管理员") e.CellStyle.ForeColor = Th.Warn;
                else e.CellStyle.ForeColor = Th.TxtPri;
            };
            dgvUsers.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) BtnUserEdit_Click(null, EventArgs.Empty); };
        }

        private void ReloadUsers()
        {
            if (dgvUsers == null) return;
            var users = Db.ListUsers(string.IsNullOrWhiteSpace(txtUserSearch.Text) ? null : txtUserSearch.Text.Trim());
            dgvUsers.Rows.Clear();

            // 统计每个借用人的在借数量（一次查询，避免逐行查库）
            var lent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in Db.ListLent(rules == null ? 24 : rules.DueWarnHours))
            {
                if (string.IsNullOrEmpty(it.Borrower)) continue;
                lent[it.Borrower] = (lent.ContainsKey(it.Borrower) ? lent[it.Borrower] : 0) + 1;
            }

            foreach (var u in users)
            {
                int n = lent.ContainsKey(u.Name) ? lent[u.Name] : 0;
                dgvUsers.Rows.Add(u.CardEpc, u.Name, u.UserNo, u.ClassName, u.Phone, u.Email,
                    u.RoleLabel, n > 0 ? n + " 件" : "—", u.Notes, u.UpdatedAt);
            }
        }

        private User SelectedUser()
        {
            if (dgvUsers == null || dgvUsers.SelectedRows.Count == 0) return null;
            var row = dgvUsers.SelectedRows[0];
            var cell = row.Cells["Card"].Value;
            string card = cell == null ? "" : cell.ToString();
            var users = Db.ListUsers(null);
            for (int i = 0; i < users.Count; i++)
            {
                var u = users[i];
                if (!string.IsNullOrEmpty(card) && string.Equals(u.CardEpc, card, StringComparison.OrdinalIgnoreCase)) return u;
            }
            // 无卡号的借用人：用姓名 + 学号定位
            var nameCell = row.Cells["Name"].Value;
            var noCell = row.Cells["No"].Value;
            string name = nameCell == null ? "" : nameCell.ToString();
            string no = noCell == null ? "" : noCell.ToString();
            for (int i = 0; i < users.Count; i++)
                if (users[i].Name == name && users[i].UserNo == no) return users[i];
            return null;
        }

        private void BtnUserAdd_Click(object sender, EventArgs e)
        {
            using (var dlg = new UserDialog(null, ReadOneCard))
                if (dlg.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadUsers(); }
        }

        private void BtnUserEdit_Click(object sender, EventArgs e)
        {
            var u = SelectedUser();
            if (u == null) { MsgBox("请先选中一行借用人记录"); return; }
            using (var dlg = new UserDialog(u, ReadOneCard))
                if (dlg.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadUsers(); }
        }

        private void BtnUserDel_Click(object sender, EventArgs e)
        {
            var u = SelectedUser();
            if (u == null) { MsgBox("请先选中一行借用人记录"); return; }
            int lent = 0;
            foreach (var it in Db.ListLent(rules == null ? 24 : rules.DueWarnHours))
                if (string.Equals(it.Borrower, u.Name, StringComparison.OrdinalIgnoreCase)) lent++;
            string extra = lent > 0 ? "\n注意：该借用人当前还有 " + lent + " 件设备未归还。" : "";
            if (MessageBox.Show(this, "确认删除借用人「" + u.Name + "」？" + extra + "\n（历史借还记录不会被删除）",
                    "删除确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { Db.DeleteUser(u.Id); } catch (Exception ex) { MainForm.ShowDbError(this, ex); return; }
            Db.AddAudit(u.CardEpc, "other", "删除借用人 " + u.Name, "操作人：本机");
            MarkPagesDirty();
            ReloadUsers();
        }

        // 刷学生卡直接建人：只填卡号，姓名等信息在对话框里补
        // 支持两种卡：设备用的 UHF 标签，或校园卡（13.56MHz NFC 读头）
        private void BtnUserReadCard_Click(object sender, EventArgs e)
        {
            string epc = ReadOneCard();
            if (string.IsNullOrEmpty(epc)) return;
            var exist = Db.FindUserByCard(epc);
            if (exist != null)
            {
                MsgBox("该卡已登记给借用人「" + exist.Name + "」，可直接使用。");
                return;
            }
            var preset = new User { CardEpc = epc, Role = "student" };
            using (var dlg = new UserDialog(preset, ReadOneEpc))
                if (dlg.ShowDialog(this) == DialogResult.OK) { MarkPagesDirty(); ReloadUsers(); }
        }
    }

    // =======================================================================
    //  UserDialog  --  借用人（学生卡）登记 / 编辑
    // =======================================================================
    internal class UserDialog : Form
    {
        public User Result;
        private readonly User editing;          // null=新增
        private readonly Func<string> readEpc;  // 回调主窗体读一张标签
        private TextBox txtCard, txtName, txtNo, txtClass, txtPhone, txtEmail, txtNotes;
        private ComboBox cboRole;

        public UserDialog(User u, Func<string> readEpcCallback)
        {
            editing = u;
            readEpc = readEpcCallback;
            bool isNew = u == null || u.Id == 0;

            Text = isNew ? "新增借用人" : "编辑借用人";
            Size = new Size(500, 430);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            BackColor = MainForm.Th.BgPanel;
            ForeColor = MainForm.Th.TxtPri;

            int y = 20;
            AddLabel("卡号(EPC)：", y);
            txtCard = AddInput(y, 220);
            txtCard.Text = u == null ? "" : u.CardEpc;
            var btnRead = new SmoothButton
            {
                Text = "读取卡号", Location = new Point(348, y - 1), Size = new Size(96, 26),
                BaseColor = MainForm.Th.Accent2, ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9, FontStyle.Bold)
            };
            btnRead.Click += (s, e) =>
            {
                if (readEpc == null) return;
                string epc = readEpc();
                if (!string.IsNullOrEmpty(epc)) txtCard.Text = epc;
            };
            Controls.Add(btnRead);

            y += 40; AddLabel("姓名：", y); txtName = AddInput(y, 320);
            if (u != null) txtName.Text = u.Name;

            y += 40; AddLabel("学号/工号：", y); txtNo = AddInput(y, 320);
            if (u != null) txtNo.Text = u.UserNo;

            y += 40; AddLabel("班级/部门：", y); txtClass = AddInput(y, 320);
            if (u != null) txtClass.Text = u.ClassName;

            y += 40; AddLabel("手机号：", y); txtPhone = AddInput(y, 320);
            if (u != null) txtPhone.Text = u.Phone;

            y += 40; AddLabel("邮箱：", y); txtEmail = AddInput(y, 320);
            if (u != null) txtEmail.Text = u.Email;

            y += 40; AddLabel("角色：", y);
            cboRole = new ComboBox
            {
                Location = new Point(120, y - 2), Width = 130, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = MainForm.Th.BgInput, ForeColor = MainForm.Th.TxtPri, FlatStyle = FlatStyle.Flat
            };
            cboRole.Items.AddRange(Roles.Labels);
            cboRole.SelectedItem = Roles.Label(u == null ? "student" : u.Role);
            if (cboRole.SelectedIndex < 0) cboRole.SelectedIndex = 0;
            Controls.Add(cboRole);

            y += 40; AddLabel("备注：", y); txtNotes = AddInput(y, 320, 2);
            if (u != null) txtNotes.Text = u.Notes;

            var tip = new Label
            {
                Text = "提示：卡号可留空（仅登记姓名）；留空后该借用人不参与刷卡识别。\n" +
                       "「读取卡号」支持两种卡：贴设备的 UHF 标签（需先连接 UHF 读写器），\n" +
                       "或校园卡/学生卡 13.56MHz（插 USB 免驱 NFC 读卡器后会自动启用）。\n" +
                       "也可以直接手动输入卡号 / 校园卡卡号。",
                ForeColor = MainForm.Th.TxtMuted, Location = new Point(20, y + 58), AutoSize = true,
                MaximumSize = new Size(440, 0)
            };
            Controls.Add(tip);

            var btnOk = new SmoothButton
            {
                Text = "保存", Location = new Point(120, 340), Size = new Size(120, 36),
                BaseColor = MainForm.Th.Accent, ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold)
            };
            btnOk.Click += BtnOk_Click;
            var btnCancel = new SmoothButton
            {
                Text = "取消", Location = new Point(250, 340), Size = new Size(120, 36),
                BaseColor = MainForm.Th.BgCard, ForeColor = MainForm.Th.TxtPri,
                Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            ActiveControl = txtName;
        }

        private void AddLabel(string t, int y)
        {
            Controls.Add(new Label { Text = t, Location = new Point(20, y + 3), AutoSize = true, ForeColor = MainForm.Th.TxtSec });
        }

        private TextBox AddInput(int y, int w, int h = 1)
        {
            var tb = new TextBox
            {
                Location = new Point(120, y), Width = w, BackColor = MainForm.Th.BgInput,
                ForeColor = MainForm.Th.TxtPri, BorderStyle = BorderStyle.FixedSingle
            };
            if (h > 1) { tb.Multiline = true; tb.Height = 22 * h; }
            Controls.Add(tb);
            return tb;
        }

        private void BtnOk_Click(object sender, EventArgs e)
        {
            string name = txtName.Text.Trim();
            if (string.IsNullOrEmpty(name)) { MessageBox.Show(this, "姓名不能为空"); return; }
            string card = txtCard.Text.Trim().ToUpperInvariant();
            int id = editing == null ? 0 : editing.Id;
            if (!string.IsNullOrEmpty(card) && Db.CardEpcTaken(card, id))
            {
                MessageBox.Show(this, "该卡号已登记给其他借用人，请检查后重试。");
                return;
            }

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            Result = new User
            {
                Id = id,
                CardEpc = card,
                Name = name,
                UserNo = txtNo.Text.Trim(),
                ClassName = txtClass.Text.Trim(),
                Phone = txtPhone == null ? "" : txtPhone.Text.Trim(),
                Email = txtEmail == null ? "" : txtEmail.Text.Trim(),
                Role = Roles.Code(cboRole.SelectedItem == null ? "学生" : cboRole.SelectedItem.ToString()),
                Notes = txtNotes.Text.Trim(),
                CreatedAt = editing != null && !string.IsNullOrEmpty(editing.CreatedAt) ? editing.CreatedAt : now,
                UpdatedAt = now,
            };
            try
            {
                if (id > 0) Db.UpdateUser(Result); else Db.InsertUser(Result);
            }
            catch (Exception ex) { MainForm.ShowDbError(this, ex); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
